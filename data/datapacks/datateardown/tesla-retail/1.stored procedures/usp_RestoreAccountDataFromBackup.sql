-- ============================================================
-- Stored Procedure: usp_RestoreAccountDataFromBackup
-- Description: Tears down existing billing data for an account
--              and restores it from prefixed backup tables.
-- Version: 1.0.0
-- ============================================================

IF OBJECT_ID('dbo.usp_RestoreAccountDataFromBackup', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_RestoreAccountDataFromBackup
GO

CREATE PROCEDURE dbo.usp_RestoreAccountDataFromBackup
    @AccountId      INT,
    @Prefix         NVARCHAR(50) = 'BKP_',   -- Must match the prefix used during backup
    @DryRun         BIT = 0,                  -- 1 = print plan only, no changes
    @Debug          BIT = 0                   -- 1 = verbose PRINT output
AS
BEGIN
    SET NOCOUNT ON
    SET XACT_ABORT ON   -- Auto-rollback on any error

    -- --------------------------------------------------------
    -- Validation
    -- --------------------------------------------------------
    IF @AccountId IS NULL OR @AccountId <= 0
    BEGIN
        RAISERROR('Invalid @AccountId. Must be a positive integer.', 16, 1)
        RETURN 1
    END

    DECLARE @SQL           NVARCHAR(MAX)
    DECLARE @TableName     NVARCHAR(200)
    DECLARE @FullPrefix    NVARCHAR(100) = @Prefix + CAST(@AccountId AS NVARCHAR(20)) + '_'
    DECLARE @RowsAffected  INT
    DECLARE @TotalDeleted  INT = 0
    DECLARE @TotalRestored INT = 0
    DECLARE @ErrorMessage  NVARCHAR(4000)

    IF @Debug = 1 OR @DryRun = 1
    BEGIN
        PRINT '========================================================'
        PRINT 'usp_RestoreAccountDataFromBackup'
        PRINT '  AccountId  : ' + CAST(@AccountId AS NVARCHAR(20))
        PRINT '  FullPrefix : ' + @FullPrefix
        PRINT '  DryRun     : ' + CAST(@DryRun AS NVARCHAR(1))
        PRINT '  Debug      : ' + CAST(@Debug AS NVARCHAR(1))
        PRINT '========================================================'
    END

    IF @DryRun = 1
    BEGIN
        PRINT ''
        PRINT '*** DRY RUN — No data will be modified ***'
        PRINT ''

        -- Show which backup tables exist
        DECLARE @CheckSQL NVARCHAR(MAX) = '
            SELECT name AS BackupTable, create_date
            FROM sys.tables
            WHERE name LIKE ''' + REPLACE(@FullPrefix, '''', '''''') + '%''
            ORDER BY name'
        EXEC sp_executesql @CheckSQL
        RETURN 0
    END

    -- --------------------------------------------------------
    -- Wrap everything in a transaction
    -- --------------------------------------------------------
    BEGIN TRY
        BEGIN TRANSACTION

        -- ====================================================
        -- PHASE 1: Delete existing data for the account
        -- ====================================================
        IF @Debug = 1 PRINT ''
        IF @Debug = 1 PRINT '========================================'
        IF @Debug = 1 PRINT 'PHASE 1: Deleting existing data for AccountId: ' + CAST(@AccountId AS NVARCHAR(20))
        IF @Debug = 1 PRINT '========================================'

        -- STEP 1: Break circular/cross FK references
        UPDATE dbo.V2_RBILL_Transaction
        SET AssociatedTransactionId = NULL,
            ReversedTransactionId   = NULL,
            PrintedOnInvoiceId      = NULL
        WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Nulled Transaction self-refs and Invoice ref: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        UPDATE dbo.V2_RBILL_Invoice
        SET InvoiceReversalId = NULL
        WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        IF @Debug = 1 PRINT '  Nulled Invoice self-ref (InvoiceReversalId): ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        UPDATE dbo.V2_RBILL_Bill
        SET ReversalNumber       = NULL,
            ReversalInvoiceId    = NULL,
            ReplacementInvoiceId = NULL,
            InvoiceId            = NULL
        WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        IF @Debug = 1 PRINT '  Nulled Bill self-ref and Invoice refs: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- STEP 2: Delete TransactionAssociation (not in backup scope)
        IF OBJECT_ID('dbo.V2_RBILL_TransactionAssociation', 'U') IS NOT NULL
        BEGIN
            DELETE ta
            FROM dbo.V2_RBILL_TransactionAssociation ta
                INNER JOIN dbo.V2_RBILL_Transaction t ON t.TransactionId = ta.TransactionId
            WHERE t.AccountId = @AccountId
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalDeleted = @TotalDeleted + @RowsAffected
            IF @Debug = 1 PRINT '  Deleted TransactionAssociation: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END

        -- STEP 3: Delete leaf/child tables first, then parents

        -- AllocationDetail -> Allocation
        DELETE ad
        FROM dbo.V2_RBILL_AllocationDetail ad
            INNER JOIN dbo.V2_RBILL_Allocation a ON a.AllocationId = ad.AllocationId
        WHERE a.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted AllocationDetail: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE FROM dbo.V2_RBILL_Allocation WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted Allocation: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- AccountBalance
        DELETE FROM dbo.V2_RBILL_AccountBalance WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted AccountBalance: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- AccountBalanceCurrent
        DELETE FROM dbo.V2_RBILL_AccountBalanceCurrent WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted AccountBalanceCurrent: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- Transaction
        DELETE FROM dbo.V2_RBILL_Transaction WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted Transaction: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- Invoice children
        DELETE pdf
        FROM dbo.V2_RBILL_InvoicePDFFileVault pdf
            INNER JOIN dbo.V2_RBILL_InvoiceFileVault fv ON pdf.InvoiceFileVaultId = fv.InvoiceFileVaultId
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = fv.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoicePDFFileVault: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE ipa
        FROM dbo.V2_RBILL_InvoicePrintAudit ipa
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ipa.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoicePrintAudit: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE ifv
        FROM dbo.V2_RBILL_InvoiceFileVault ifv
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ifv.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoiceFileVault: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE ps
        FROM dbo.V2_RBILL_InvoicePrintStatusCurrent ps
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ps.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoicePrintStatusCurrent: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE sh
        FROM dbo.V2_RBILL_InvoicePrintStatusHistory sh
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = sh.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoicePrintStatusHistory: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE ir
        FROM dbo.V2_RBILL_InvoiceReversal ir
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ir.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoiceReversal: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE ih
        FROM dbo.V2_RBILL_InvoiceStatusHistory ih
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ih.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoiceStatusHistory: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE ie
        FROM dbo.V2_RBILL_InvoiceEvent ie
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ie.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoiceEvent: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE ic
        FROM dbo.V2_RBILL_InvoiceChargeCategory ic
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ic.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoiceChargeCategory: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE d
        FROM dbo.V2_RBILL_InvoiceDunning d
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = d.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoiceDunning: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE iot
        FROM dbo.V2_RBILL_InvoiceOutput iot
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = iot.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted InvoiceOutput: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE s
        FROM dbo.V2_RBILL_StatementInvoice s
            INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = s.InvoiceId
        WHERE i.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted StatementInvoice: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- Bill children then Bill
        DELETE ba
        FROM dbo.V2_RBILL_BillAdjustment ba
            INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = ba.OriginalBillId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted BillAdjustment: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE bl
        FROM dbo.V2_RBILL_BillLine bl
            INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = bl.BillId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted BillLine: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE bg
        FROM dbo.V2_RBILL_BillGl bg
            INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = bg.BillId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted BillGl: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE br
        FROM dbo.V2_RBILL_BillReading br
            INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = br.BillId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted BillReading: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE cd
        FROM dbo.V2_RBILL_BillCfdSettlement cd
            INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = cd.BillId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted BillCfdSettlement: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- RescheduledBill (both FK columns)
        DELETE cd
        FROM dbo.V2_RBILL_RescheduledBill cd
            INNER JOIN dbo.V2_RBILL_Bill b ON cd.RescheduledBillId = b.BillId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted RescheduledBill (RescheduledBillId): ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE cd
        FROM dbo.V2_RBILL_RescheduledBill cd
            INNER JOIN dbo.V2_RBILL_Bill b ON cd.OriginalBillId = b.BillId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted RescheduledBill (OriginalBillId): ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE FROM dbo.V2_RBILL_Bill WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted Bill: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        -- Collection print status tables
        DELETE cd
        FROM dbo.V2_RBILL_CollectionPrintStatusCurrent cd
            INNER JOIN dbo.V2_RBILL_Invoice b ON cd.InvoiceId = b.InvoiceId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted CollectionPrintStatusCurrent: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE cd
        FROM dbo.V2_RBILL_CollectionPrintStatusHistory cd
            INNER JOIN dbo.V2_RBILL_Invoice b ON cd.InvoiceId = b.InvoiceId
        WHERE b.AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted CollectionPrintStatusHistory: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        DELETE FROM dbo.V2_RBILL_PendingTransaction WHERE AccountId = @AccountId
		SET @RowsAffected = @@ROWCOUNT        
		IF @Debug = 1 PRINT '  Deleted PendingTransaction: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'    

        -- Finally Invoice
        DELETE FROM dbo.V2_RBILL_Invoice WHERE AccountId = @AccountId
        SET @RowsAffected = @@ROWCOUNT
        SET @TotalDeleted = @TotalDeleted + @RowsAffected
        IF @Debug = 1 PRINT '  Deleted Invoice: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

        IF @Debug = 1
        BEGIN
            PRINT ''
            PRINT 'PHASE 1 complete. Total rows deleted/nulled: ' + CAST(@TotalDeleted AS NVARCHAR(20))
        END

        -- ====================================================
        -- PHASE 2: Restore data from backup tables
        -- ====================================================
        IF @Debug = 1
        BEGIN
            PRINT ''
            PRINT '========================================'
            PRINT 'PHASE 2: Restoring data from backup tables'
            PRINT 'Full prefix: ' + @FullPrefix
            PRINT '========================================'
        END

        -- --------------------------------------------------------
        -- 1. V2_RBILL_Invoice
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_Invoice'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_Invoice...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_Invoice ON

            INSERT INTO dbo.V2_RBILL_Invoice (
                InvoiceId, InvoiceTypeId, AccountId, InvoiceBatchId, InvoiceStatusId, InvoiceTemplateId,
                InvoiceReversalId, OpeningBalance, PaymentsReceived, PaymentDue, TotalCostExc, Tax,
                TotalCostInc, TotalAdjustmentsExc, TotalAdjustmentsTax, TotalAdjustmentsInc,
                TotalInvoiceAmount, InvoiceInfo, IsPosted, CreateAndReturn, RevisedPaymentDueDate,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, DateRevisionCategoryId, CollectionDueDate,
                IssueDate, SendAdditionalDocumentation, PrintedDueDate,
                NumberOfCustomerPaymentExtensionsProcessed, NumberOfInternalPaymentExtensionsProcessed,
                PaymentExtensionProcessedByExternalUser, UnpaidAmount, BestOfferMessage,
                BestOfferProductCode, PaidAmount, InvoicePaymentStatus, InvoiceDetail
            )
            SELECT
                InvoiceId, InvoiceTypeId, AccountId, InvoiceBatchId, InvoiceStatusId, InvoiceTemplateId,
                InvoiceReversalId, OpeningBalance, PaymentsReceived, PaymentDue, TotalCostExc, Tax,
                TotalCostInc, TotalAdjustmentsExc, TotalAdjustmentsTax, TotalAdjustmentsInc,
                TotalInvoiceAmount, InvoiceInfo, IsPosted, CreateAndReturn, RevisedPaymentDueDate,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, DateRevisionCategoryId, CollectionDueDate,
                IssueDate, SendAdditionalDocumentation, PrintedDueDate,
                NumberOfCustomerPaymentExtensionsProcessed, NumberOfInternalPaymentExtensionsProcessed,
                PaymentExtensionProcessedByExternalUser, UnpaidAmount, BestOfferMessage,
                BestOfferProductCode, PaidAmount, InvoicePaymentStatus, InvoiceDetail
            FROM dbo.' + @FullPrefix + 'V2_RBILL_Invoice

            SET IDENTITY_INSERT dbo.V2_RBILL_Invoice OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_Invoice restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 2. V2_RBILL_Bill
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_Bill'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_Bill...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_Bill ON

            INSERT INTO dbo.V2_RBILL_Bill (
                BillId, BillStatusId, BillBatchId, AccountId, CustomerId, SiteId, ConnectionPointId,
                ContractId, MarketParticipantId, InvoiceId, ReversalInvoiceId, ReplacementInvoiceId,
                StartDate, EndDate, TotalCostExc, Tax, TotalCostInc, PaymentDue, ReversalNumber,
                ReversalReason, IsSimulated, IgnoreErrors, WorkItemId, ForceInvoice, Adhoc, IsOnHold,
                NSRD, InvoiceMessage, BillValidationStatusId, BillValidationMessage, CreatedBy,
                CreatedOn, ModifiedBy, ModifiedOn, ContainsAdHocBillLines, IsFinalBill, IsWashup
            )
            SELECT
                BillId, BillStatusId, BillBatchId, AccountId, CustomerId, SiteId, ConnectionPointId,
                ContractId, MarketParticipantId, InvoiceId, ReversalInvoiceId, ReplacementInvoiceId,
                StartDate, EndDate, TotalCostExc, Tax, TotalCostInc, PaymentDue, ReversalNumber,
                ReversalReason, IsSimulated, IgnoreErrors, WorkItemId, ForceInvoice, Adhoc, IsOnHold,
                NSRD, InvoiceMessage, BillValidationStatusId, BillValidationMessage, CreatedBy,
                CreatedOn, ModifiedBy, ModifiedOn, ContainsAdHocBillLines, IsFinalBill, IsWashup
            FROM dbo.' + @FullPrefix + 'V2_RBILL_Bill

            SET IDENTITY_INSERT dbo.V2_RBILL_Bill OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_Bill restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 3. V2_RBILL_BillLine
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_BillLine'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_BillLine...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_BillLine ON

            INSERT INTO dbo.V2_RBILL_BillLine (
                BillLineId, BillId, LineNumber, ProductId, TariffId, ChargeId, FunctionId,
                UnitOfMeasureId, AccountContractId, ContractId, LoadGroupProductId, TariffDescription,
                Volume, Mlf, Dlf, CostPerUnit, Cost, Tax, StartDate, EndDate, TransactionRef,
                ReversalTransaction, SiteReference, GlCodeId, ReversedGLCodeId, PaymentId,
                AdjustmentIndicator, AdjustmentReason, TimeOfDayCodeChildId, TimeOfDayCodeChild,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, BuildingContractId, IsChargeable,
                IsPayOnTimeDiscount, IsPrepaid, ContractBillLineId, IsCarbonCharge,
                CarbonFloatingAciId
            )
            SELECT
                BillLineId, BillId, LineNumber, ProductId, TariffId, ChargeId, FunctionId,
                UnitOfMeasureId, AccountContractId, ContractId, LoadGroupProductId, TariffDescription,
                Volume, Mlf, Dlf, CostPerUnit, Cost, Tax, StartDate, EndDate, TransactionRef,
                ReversalTransaction, SiteReference, GlCodeId, ReversedGLCodeId, PaymentId,
                AdjustmentIndicator, AdjustmentReason, TimeOfDayCodeChildId, TimeOfDayCodeChild,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, BuildingContractId, IsChargeable,
                IsPayOnTimeDiscount, IsPrepaid, ContractBillLineId, IsCarbonCharge,
                CarbonFloatingAciId
            FROM dbo.' + @FullPrefix + 'V2_RBILL_BillLine

            SET IDENTITY_INSERT dbo.V2_RBILL_BillLine OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_BillLine restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 4. V2_RBILL_BillGl
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_BillGl'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_BillGl...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_BillGl ON

            INSERT INTO dbo.V2_RBILL_BillGl (
                BillGlId, BillId, GlCodeId, Cost, Volume, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            )
            SELECT
                BillGlId, BillId, GlCodeId, Cost, Volume, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_BillGl

            SET IDENTITY_INSERT dbo.V2_RBILL_BillGl OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_BillGl restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 5. V2_RBILL_BillReading
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_BillReading'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_BillReading...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_BillReading ON

            INSERT INTO dbo.V2_RBILL_BillReading (
                BillReadingId, BillId, ReadId, MeterSerial, UOM, StartDate, EndDate, StartReading,
                EndReading, Quantity, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, ReadQualityTypeId,
                IsBasicMeter, RegisterId, Multiplier, AverageHeatingValue, PressureCorrectionFactor,
                MarketIdentifier, DLF, MLF, HighestDemandValue, HighestDemandUom, HighestDemandOccurredOn
            )
            SELECT
                BillReadingId, BillId, ReadId, MeterSerial, UOM, StartDate, EndDate, StartReading,
                EndReading, Quantity, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, ReadQualityTypeId,
                IsBasicMeter, RegisterId, Multiplier, AverageHeatingValue, PressureCorrectionFactor,
                MarketIdentifier, DLF, MLF, HighestDemandValue, HighestDemandUom, HighestDemandOccurredOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_BillReading

            SET IDENTITY_INSERT dbo.V2_RBILL_BillReading OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_BillReading restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 6. V2_RBILL_BillAdjustment
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_BillAdjustment'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_BillAdjustment...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_BillAdjustment ON

            INSERT INTO dbo.V2_RBILL_BillAdjustment (
                BillAdjustmentId, OriginalBilLlineId, OriginalBillId, AppliedToBillLineId,
                AppliedToBillId, TariffDescription, Volume, CostPerUnit, Cost, Tax, StartDate,
                EndDate, AdjustmentReason, QuantityFunctionId, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            )
            SELECT
                BillAdjustmentId, OriginalBilLlineId, OriginalBillId, AppliedToBillLineId,
                AppliedToBillId, TariffDescription, Volume, CostPerUnit, Cost, Tax, StartDate,
                EndDate, AdjustmentReason, QuantityFunctionId, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_BillAdjustment

            SET IDENTITY_INSERT dbo.V2_RBILL_BillAdjustment OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_BillAdjustment restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 7. V2_RBILL_BillCfdSettlement
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_BillCfdSettlement'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_BillCfdSettlement...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_BillCfdSettlement ON

            INSERT INTO dbo.V2_RBILL_BillCfdSettlement (
                BillCfdSettlementId, BillId, SettlementDetails, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            )
            SELECT
                BillCfdSettlementId, BillId, SettlementDetails, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_BillCfdSettlement

            SET IDENTITY_INSERT dbo.V2_RBILL_BillCfdSettlement OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_BillCfdSettlement restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 8. V2_RBILL_InvoiceReversal
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceReversal'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoiceReversal...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceReversal ON

            INSERT INTO dbo.V2_RBILL_InvoiceReversal (
                InvoiceReversalId, InvoiceId, ReversalReason, ReversalComment, Reissue, DisablePrint,
                ReverseChildInvoice, CreatedBy, CreatedOn
            )
            SELECT
                InvoiceReversalId, InvoiceId, ReversalReason, ReversalComment, Reissue, DisablePrint,
                ReverseChildInvoice, CreatedBy, CreatedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoiceReversal

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceReversal OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoiceReversal restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 9. V2_RBILL_InvoiceStatusHistory
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceStatusHistory'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoiceStatusHistory...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceStatusHistory ON

            INSERT INTO dbo.V2_RBILL_InvoiceStatusHistory (
                InvoiceStatusHistoryId, InvoiceId, InvoiceStatusId, CreatedBy, CreatedOn
            )
            SELECT
                InvoiceStatusHistoryId, InvoiceId, InvoiceStatusId, CreatedBy, CreatedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoiceStatusHistory

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceStatusHistory OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoiceStatusHistory restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 10. V2_RBILL_InvoiceEvent
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceEvent'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoiceEvent...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceEvent ON

            INSERT INTO dbo.V2_RBILL_InvoiceEvent (
                InvoiceEventId, InvoiceId, EventId, EventDate, EventDescription, CreatedBy, CreatedOn,
                ModifiedBy, ModifiedOn
            )
            SELECT
                InvoiceEventId, InvoiceId, EventId, EventDate, EventDescription, CreatedBy, CreatedOn,
                ModifiedBy, ModifiedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoiceEvent

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceEvent OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoiceEvent restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 11. V2_RBILL_InvoiceChargeCategory
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceChargeCategory'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoiceChargeCategory...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceChargeCategory ON

            INSERT INTO dbo.V2_RBILL_InvoiceChargeCategory (
                InvoiceChargeCategoryId, InvoiceId, TotalExcGst, Gst, Volume, UomId, CreatedBy,
                CreatedOn, ModifiedBy, ModifiedOn, ChargeCategoryId
            )
            SELECT
                InvoiceChargeCategoryId, InvoiceId, TotalExcGst, Gst, Volume, UomId, CreatedBy,
                CreatedOn, ModifiedBy, ModifiedOn, ChargeCategoryId
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoiceChargeCategory

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceChargeCategory OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoiceChargeCategory restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 12. V2_RBILL_InvoiceDunning
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceDunning'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoiceDunning...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceDunning ON

            INSERT INTO dbo.V2_RBILL_InvoiceDunning (
                InvoiceDunningId, InvoiceId, CustomerId, AccountId, IsPaid, IsPaused, IsReversed,
                IsRemoved, IsOnPaymentPlan, IsOnLifeSupport, HaveSentCourtesyNotice,
                HaveSentReminderNotice, HaveSentFinalNotice, HaveResentInvoice, HaveOfferedPaymentPlan,
                HaveSentDisconnectionNotice, HaveMadeInitialContact, HaveMadeFinalContact,
                HaveCheckedLifeSupport, HaveArrangedDisconnection, CourtesyNoticeSentDate,
                ReminderNoticeSentDate, FinalNoticeSentDate, InvoiceResentDate, PaymentPlanOfferedOn,
                InitialContactMadeOn, FinalContactMadeOn, DisconnectionNoticeDate, DisconnectionDate,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn,
                CourtesyNoticeDueDate, ReminderNoticeDueDate, FinalNoticeDueDate,
                ReminderNoticeInvoiceBalance, FinalNoticeAccountBalance, EventIdPaused,
                EventIdPausedDate, ReminderNoticeFromModelVersionForkTransitionId,
                ReminderNoticeToModelVersionForkTransitionId,
                FinalNoticeFromModelVersionForkTransitionId,
                FinalNoticeToModelVersionForkTransitionId, TotalAmountPayable,
                CourtesyNoticeDate, ReminderNoticeDate, FinalNoticeDate,
                CourtesyNoticeXmlBatchDate, ReminderNoticeXmlBatchDate, FinalNoticeXmlBatchDate
            )
            SELECT
                InvoiceDunningId, InvoiceId, CustomerId, AccountId, IsPaid, IsPaused, IsReversed,
                IsRemoved, IsOnPaymentPlan, IsOnLifeSupport, HaveSentCourtesyNotice,
                HaveSentReminderNotice, HaveSentFinalNotice, HaveResentInvoice, HaveOfferedPaymentPlan,
                HaveSentDisconnectionNotice, HaveMadeInitialContact, HaveMadeFinalContact,
                HaveCheckedLifeSupport, HaveArrangedDisconnection, CourtesyNoticeSentDate,
                ReminderNoticeSentDate, FinalNoticeSentDate, InvoiceResentDate, PaymentPlanOfferedOn,
                InitialContactMadeOn, FinalContactMadeOn, DisconnectionNoticeDate, DisconnectionDate,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn,
                CourtesyNoticeDueDate, ReminderNoticeDueDate, FinalNoticeDueDate,
                ReminderNoticeInvoiceBalance, FinalNoticeAccountBalance, EventIdPaused,
                EventIdPausedDate, ReminderNoticeFromModelVersionForkTransitionId,
                ReminderNoticeToModelVersionForkTransitionId,
                FinalNoticeFromModelVersionForkTransitionId,
                FinalNoticeToModelVersionForkTransitionId, TotalAmountPayable,
                CourtesyNoticeDate, ReminderNoticeDate, FinalNoticeDate,
                CourtesyNoticeXmlBatchDate, ReminderNoticeXmlBatchDate, FinalNoticeXmlBatchDate
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoiceDunning

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceDunning OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoiceDunning restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 13. V2_RBILL_InvoiceOutput
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceOutput'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoiceOutput...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceOutput ON

            INSERT INTO dbo.V2_RBILL_InvoiceOutput (
                InvoiceOutputId, InvoiceId, InvoiceBatchId, InvoiceXmlFormatId, PrintStatusId,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            )
            SELECT
                InvoiceOutputId, InvoiceId, InvoiceBatchId, InvoiceXmlFormatId, PrintStatusId,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoiceOutput

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceOutput OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoiceOutput restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 14. V2_RBILL_InvoiceFileVault
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceFileVault'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoiceFileVault...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceFileVault ON

            INSERT INTO dbo.V2_RBILL_InvoiceFileVault (
                InvoiceFileVaultId, InvoiceId, InvoiceBatchedFileVaultId, Description, FileName,
                FileType, FileData, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, InvoiceEventId, GUID
            )
            SELECT
                InvoiceFileVaultId, InvoiceId, InvoiceBatchedFileVaultId, Description, FileName,
                FileType, FileData, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, InvoiceEventId, GUID
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoiceFileVault

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoiceFileVault OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoiceFileVault restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 15. V2_RBILL_InvoicePDFFileVault
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePDFFileVault'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoicePDFFileVault...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePDFFileVault ON

            INSERT INTO dbo.V2_RBILL_InvoicePDFFileVault (
                InvoicePDFFileVaultId, InvoiceFileVaultId, Description, FileName, FileType, FileData,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, InvoiceEventId
            )
            SELECT
                InvoicePDFFileVaultId, InvoiceFileVaultId, Description, FileName, FileType, FileData,
                CreatedBy, CreatedOn, ModifiedBy, ModifiedOn, InvoiceEventId
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoicePDFFileVault

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePDFFileVault OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoicePDFFileVault restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 16. V2_RBILL_InvoicePrintAudit
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintAudit'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoicePrintAudit...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePrintAudit ON

            INSERT INTO dbo.V2_RBILL_InvoicePrintAudit (
                InvoicePrintAuditId, InvoiceId, InvoiceFileVaultId, InvoiceType, DateXMLCreated,
                DueDate, FinalNoticeDate, OpeningBalance, PaymentsReceived, OverdueBalance, NewCharges,
                PayOnTimeDiscount, PayLate, TotalDue, TotalIncGST, GST, TotalExclGST, CreatedBy,
                CreatedOn, ModifiedBy, ModifiedOn
            )
            SELECT
                InvoicePrintAuditId, InvoiceId, InvoiceFileVaultId, InvoiceType, DateXMLCreated,
                DueDate, FinalNoticeDate, OpeningBalance, PaymentsReceived, OverdueBalance, NewCharges,
                PayOnTimeDiscount, PayLate, TotalDue, TotalIncGST, GST, TotalExclGST, CreatedBy,
                CreatedOn, ModifiedBy, ModifiedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoicePrintAudit

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePrintAudit OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoicePrintAudit restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 17. V2_RBILL_InvoicePrintStatusCurrent
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintStatusCurrent'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoicePrintStatusCurrent...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePrintStatusCurrent ON

            INSERT INTO dbo.V2_RBILL_InvoicePrintStatusCurrent (
                InvoicePrintStatusCurrentId, InvoiceId, PrintStatusId, CreatedOn
            )
            SELECT
                InvoicePrintStatusCurrentId, InvoiceId, PrintStatusId, CreatedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoicePrintStatusCurrent

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePrintStatusCurrent OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoicePrintStatusCurrent restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 18. V2_RBILL_InvoicePrintStatusHistory
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintStatusHistory'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_InvoicePrintStatusHistory...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePrintStatusHistory ON

            INSERT INTO dbo.V2_RBILL_InvoicePrintStatusHistory (
                InvoicePrintStatusHistoryId, InvoiceId, PrintStatusId, CreatedBy, CreatedOn
            )
            SELECT
                InvoicePrintStatusHistoryId, InvoiceId, PrintStatusId, CreatedBy, CreatedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_InvoicePrintStatusHistory

            SET IDENTITY_INSERT dbo.V2_RBILL_InvoicePrintStatusHistory OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_InvoicePrintStatusHistory restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 19. V2_RBILL_StatementInvoice
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_StatementInvoice'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_StatementInvoice...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_StatementInvoice ON

            INSERT INTO dbo.V2_RBILL_StatementInvoice (
                StatementInvoiceId, StatementId, InvoiceId, InvoiceAmount, CreatedBy, CreatedOn,
                ModifiedBy, ModifiedOn, ProcessedOn
            )
            SELECT
                StatementInvoiceId, StatementId, InvoiceId, InvoiceAmount, CreatedBy, CreatedOn,
                ModifiedBy, ModifiedOn, ProcessedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_StatementInvoice

            SET IDENTITY_INSERT dbo.V2_RBILL_StatementInvoice OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_StatementInvoice restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 20. V2_RBILL_Transaction (two-phase: insert then update self-refs)
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_Transaction'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_Transaction...'

            -- Disable triggers to prevent RBILL_AccountBalance trigger
            EXEC('DISABLE TRIGGER ALL ON dbo.V2_RBILL_Transaction')
            IF @Debug = 1 PRINT '  Disabled triggers on V2_RBILL_Transaction'

            -- Phase A: Insert with self-refs as NULL
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_Transaction ON

            INSERT INTO dbo.V2_RBILL_Transaction (
                TransactionId, AccountId, AccountTransactionLineNumber, TransactionTypeId,
                TransactionDescription, DetailId, Amount, PrintedOnInvoiceId, ReversedTransactionId,
                AssociatedTransactionId, IsPaid, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn,
                IsActualAdjustment, IsImmediateGlEntryRequired
            )
            SELECT
                TransactionId, AccountId, AccountTransactionLineNumber, TransactionTypeId,
                TransactionDescription, DetailId, Amount, PrintedOnInvoiceId, NULL,
                NULL, IsPaid, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn,
                IsActualAdjustment, IsImmediateGlEntryRequired
            FROM dbo.' + @FullPrefix + 'V2_RBILL_Transaction

            SET IDENTITY_INSERT dbo.V2_RBILL_Transaction OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_Transaction inserted: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'

            -- Phase B: Update self-referencing columns
            SET @SQL = '
            UPDATE t
            SET t.ReversedTransactionId    = bk.ReversedTransactionId,
                t.AssociatedTransactionId   = bk.AssociatedTransactionId
            FROM dbo.V2_RBILL_Transaction t
            INNER JOIN dbo.' + @FullPrefix + 'V2_RBILL_Transaction bk ON bk.TransactionId = t.TransactionId
            WHERE t.AccountId = ' + CAST(@AccountId AS NVARCHAR(20)) + '
              AND (bk.ReversedTransactionId IS NOT NULL OR bk.AssociatedTransactionId IS NOT NULL)'
            EXEC sp_executesql @SQL
            IF @Debug = 1 PRINT '  V2_RBILL_Transaction self-refs updated: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

            -- Re-enable triggers
            EXEC('ENABLE TRIGGER ALL ON dbo.V2_RBILL_Transaction')
            IF @Debug = 1 PRINT '  Re-enabled triggers on V2_RBILL_Transaction'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 21. V2_RBILL_AccountBalance
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_AccountBalance'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_AccountBalance...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_AccountBalance ON

            INSERT INTO dbo.V2_RBILL_AccountBalance (
                AccountBalanceId, AccountId, TransactionId, LineNumber, Balance, CreatedOn
            )
            SELECT
                AccountBalanceId, AccountId, TransactionId, LineNumber, Balance, CreatedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_AccountBalance

            SET IDENTITY_INSERT dbo.V2_RBILL_AccountBalance OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_AccountBalance restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 22. V2_RBILL_AccountBalanceCurrent
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_AccountBalanceCurrent'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_AccountBalanceCurrent...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_AccountBalanceCurrent ON

            INSERT INTO dbo.V2_RBILL_AccountBalanceCurrent (
                AccountBalanceCurrentId, AccountId, Balance, CreatedOn, OverdueBalance
            )
            SELECT
                AccountBalanceCurrentId, AccountId, Balance, CreatedOn, OverdueBalance
            FROM dbo.' + @FullPrefix + 'V2_RBILL_AccountBalanceCurrent

            SET IDENTITY_INSERT dbo.V2_RBILL_AccountBalanceCurrent OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_AccountBalanceCurrent restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 23. V2_RBILL_Allocation
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_Allocation'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_Allocation...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_Allocation ON

            INSERT INTO dbo.V2_RBILL_Allocation (
                AllocationId, AccountId, StatusCode, ParentType, ParentId, AllocationType,
                AllocatedToId, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            )
            SELECT
                AllocationId, AccountId, StatusCode, ParentType, ParentId, AllocationType,
                AllocatedToId, CreatedBy, CreatedOn, ModifiedBy, ModifiedOn
            FROM dbo.' + @FullPrefix + 'V2_RBILL_Allocation

            SET IDENTITY_INSERT dbo.V2_RBILL_Allocation OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_Allocation restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- --------------------------------------------------------
        -- 24. V2_RBILL_AllocationDetail
        -- --------------------------------------------------------
        SET @TableName = @FullPrefix + 'V2_RBILL_AllocationDetail'
        IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
        BEGIN
            IF @Debug = 1 PRINT 'Restoring V2_RBILL_AllocationDetail...'
            SET @SQL = '
            SET IDENTITY_INSERT dbo.V2_RBILL_AllocationDetail ON

            INSERT INTO dbo.V2_RBILL_AllocationDetail (
                AllocationDetailId, AllocationId, TransactionId, AllocatedAmount
            )
            SELECT
                AllocationDetailId, AllocationId, TransactionId, AllocatedAmount
            FROM dbo.' + @FullPrefix + 'V2_RBILL_AllocationDetail

            SET IDENTITY_INSERT dbo.V2_RBILL_AllocationDetail OFF'
            EXEC sp_executesql @SQL
            SET @RowsAffected = @@ROWCOUNT
            SET @TotalRestored = @TotalRestored + @RowsAffected
            IF @Debug = 1 PRINT '  V2_RBILL_AllocationDetail restored: ' + CAST(@RowsAffected AS NVARCHAR(20)) + ' rows'
        END
        ELSE IF @Debug = 1
            PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'

        -- ====================================================
        -- COMMIT
        -- ====================================================
        COMMIT TRANSACTION

        IF @Debug = 1
        BEGIN
            PRINT ''
            PRINT '========================================'
            PRINT 'Restore COMMITTED for AccountId: ' + CAST(@AccountId AS NVARCHAR(20))
            PRINT 'Total rows restored: ' + CAST(@TotalRestored AS NVARCHAR(20))
            PRINT 'Full prefix used: ' + @FullPrefix
            PRINT '========================================'
        END

        RETURN 0

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION

        -- Re-enable triggers if they were disabled
        IF OBJECT_ID('dbo.V2_RBILL_Transaction', 'U') IS NOT NULL
        BEGIN
            BEGIN TRY
                EXEC('ENABLE TRIGGER ALL ON dbo.V2_RBILL_Transaction')
            END TRY
            BEGIN CATCH
                -- Swallow; primary error takes precedence
            END CATCH
        END

        SET @ErrorMessage = 'usp_RestoreAccountDataFromBackup failed: '
            + ERROR_MESSAGE()
            + ' (Line ' + CAST(ERROR_LINE() AS NVARCHAR(10)) + ')'

        RAISERROR(@ErrorMessage, 16, 1)
        RETURN 1
    END CATCH
END
GO