-- ============================================================
-- Restore Account Data from Backup Tables (V3 - FIXED)
-- ============================================================
-- Set your parameters here:
DECLARE @AccountId INT = 106
DECLARE @Prefix NVARCHAR(50) = 'BKP_'  -- Must match the prefix used during backup
-- ============================================================
-- The full prefix will be: @Prefix + @AccountId + '_'
-- Example: BKP_317774_V2_RBILL_Invoice
-- ============================================================

DECLARE @SQL NVARCHAR(MAX)
DECLARE @TableName NVARCHAR(200)
DECLARE @FullPrefix NVARCHAR(100) = @Prefix + CAST(@AccountId AS NVARCHAR(20)) + '_'

PRINT '========================================'
PRINT 'PHASE 1: Deleting existing data for AccountId: ' + CAST(@AccountId AS NVARCHAR(20))
PRINT '========================================'

-- ============================================================
-- STEP 1: Break circular/cross FK references by NULLing out
--         columns that point back to other tables we need to delete
-- ============================================================

-- Transaction has self-references and a reference to Invoice
-- NULL these out so we can delete freely afterwards
UPDATE dbo.V2_RBILL_Transaction
SET AssociatedTransactionId = NULL,
    ReversedTransactionId = NULL,
    PrintedOnInvoiceId = NULL
WHERE AccountId = @AccountId
PRINT '  Nulled Transaction self-refs and Invoice ref: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- Invoice has a self-reference via InvoiceReversalId
UPDATE dbo.V2_RBILL_Invoice
SET InvoiceReversalId = NULL
WHERE AccountId = @AccountId
PRINT '  Nulled Invoice self-ref (InvoiceReversalId): ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- Bill has self-reference via ReversalNumber and references to Invoice
UPDATE dbo.V2_RBILL_Bill
SET ReversalNumber = NULL,
    ReversalInvoiceId = NULL,
    ReplacementInvoiceId = NULL,
    InvoiceId = NULL
WHERE AccountId = @AccountId
PRINT '  Nulled Bill self-ref and Invoice refs: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- ============================================================
-- STEP 2: Delete TransactionAssociation (not in backup scope
--         but references Transaction and blocks its delete)
-- ============================================================
IF OBJECT_ID('dbo.V2_RBILL_TransactionAssociation', 'U') IS NOT NULL
BEGIN
    DELETE ta FROM dbo.V2_RBILL_TransactionAssociation ta
        INNER JOIN dbo.V2_RBILL_Transaction t ON t.TransactionId = ta.TransactionId
        WHERE t.AccountId = @AccountId
    PRINT '  Deleted TransactionAssociation: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END

-- ============================================================
-- STEP 3: Delete leaf/child tables first, then parents
-- ============================================================

-- AllocationDetail -> Allocation
DELETE ad FROM dbo.V2_RBILL_AllocationDetail ad
    INNER JOIN dbo.V2_RBILL_Allocation a ON a.AllocationId = ad.AllocationId
    WHERE a.AccountId = @AccountId
PRINT '  Deleted AllocationDetail: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE FROM dbo.V2_RBILL_Allocation WHERE AccountId = @AccountId
PRINT '  Deleted Allocation: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- AccountBalance -> Transaction
DELETE FROM dbo.V2_RBILL_AccountBalance WHERE AccountId = @AccountId
PRINT '  Deleted AccountBalance: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- AccountBalanceCurrent
DELETE FROM dbo.V2_RBILL_AccountBalanceCurrent WHERE AccountId = @AccountId
PRINT '  Deleted AccountBalanceCurrent: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- Transaction (self-refs already nulled, TransactionAssociation already deleted)
DELETE FROM dbo.V2_RBILL_Transaction WHERE AccountId = @AccountId
PRINT '  Deleted Transaction: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- Invoice children
DELETE pdf FROM dbo.V2_RBILL_InvoicePDFFileVault pdf
    INNER JOIN dbo.V2_RBILL_InvoiceFileVault fv ON pdf.InvoiceFileVaultId = fv.InvoiceFileVaultId
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = fv.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoicePDFFileVault: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE ipa FROM dbo.V2_RBILL_InvoicePrintAudit ipa
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ipa.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoicePrintAudit: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE ifv FROM dbo.V2_RBILL_InvoiceFileVault ifv
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ifv.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoiceFileVault: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE ps FROM dbo.V2_RBILL_InvoicePrintStatusCurrent ps
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ps.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoicePrintStatusCurrent: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE sh FROM dbo.V2_RBILL_InvoicePrintStatusHistory sh
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = sh.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoicePrintStatusHistory: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE ir FROM dbo.V2_RBILL_InvoiceReversal ir
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ir.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoiceReversal: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE ih FROM dbo.V2_RBILL_InvoiceStatusHistory ih
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ih.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoiceStatusHistory: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE ie FROM dbo.V2_RBILL_InvoiceEvent ie
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ie.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoiceEvent: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE ic FROM dbo.V2_RBILL_InvoiceChargeCategory ic
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ic.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoiceChargeCategory: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE d FROM dbo.V2_RBILL_InvoiceDunning d
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = d.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoiceDunning: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE iot FROM dbo.V2_RBILL_InvoiceOutput iot
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = iot.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted InvoiceOutput: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE s FROM dbo.V2_RBILL_StatementInvoice s
    INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = s.InvoiceId
    WHERE i.AccountId = @AccountId
PRINT '  Deleted StatementInvoice: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-- Bill children then Bill
DELETE ba FROM dbo.V2_RBILL_BillAdjustment ba
    INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = ba.OriginalBillId
    WHERE b.AccountId = @AccountId
PRINT '  Deleted BillAdjustment: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE bl FROM dbo.V2_RBILL_BillLine bl
    INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = bl.BillId
    WHERE b.AccountId = @AccountId
PRINT '  Deleted BillLine: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE bg FROM dbo.V2_RBILL_BillGl bg
    INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = bg.BillId
    WHERE b.AccountId = @AccountId
PRINT '  Deleted BillGl: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE br FROM dbo.V2_RBILL_BillReading br
    INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = br.BillId
    WHERE b.AccountId = @AccountId
PRINT '  Deleted BillReading: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE cd FROM dbo.V2_RBILL_BillCfdSettlement cd
    INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = cd.BillId
    WHERE b.AccountId = @AccountId
PRINT '  Deleted BillCfdSettlement: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

-------------------

DELETE cd FROM [dbo].[V2_RBILL_RescheduledBill] cd
INNER JOIN dbo.V2_RBILL_Bill b ON cd.RescheduledBillId = b.BillId
WHERE b.AccountId = @AccountId
PRINT '  Deleted RescheduledBill-RescheduledBillId: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'


DELETE cd FROM [dbo].[V2_RBILL_RescheduledBill] cd
INNER JOIN dbo.V2_RBILL_Bill b ON cd.OriginalBillId = b.BillId
WHERE b.AccountId = @AccountId
PRINT '  Deleted RescheduledBill-OriginalBillId: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'


------------------



DELETE FROM dbo.V2_RBILL_Bill WHERE AccountId = @AccountId
PRINT '  Deleted Bill: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

------------------

DELETE cd FROM [dbo].[V2_RBILL_CollectionPrintStatusCurrent] cd
INNER JOIN dbo.V2_RBILL_Invoice b ON cd.InvoiceId = b.InvoiceId
WHERE b.AccountId = @AccountId
PRINT '  Deleted CollectionPrintStatusCurrent: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

DELETE cd FROM [dbo].[V2_RBILL_CollectionPrintStatusHistory] cd
INNER JOIN dbo.V2_RBILL_Invoice b ON cd.InvoiceId = b.InvoiceId
WHERE b.AccountId = @AccountId
PRINT '  Deleted CollectionPrintStatusHistory: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

------------------


-- Finally Invoice (all references now cleared)
DELETE FROM dbo.V2_RBILL_Invoice WHERE AccountId = @AccountId
PRINT '  Deleted Invoice: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'


PRINT ''
PRINT '========================================'
PRINT 'PHASE 2: Restoring data from backup tables'
PRINT 'Full prefix: ' + @FullPrefix
PRINT '========================================'

-- ============================================================
-- 1. V2_RBILL_Invoice (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_Invoice'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_Invoice...'
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
    PRINT '  V2_RBILL_Invoice restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 2. V2_RBILL_Bill (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_Bill'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_Bill...'
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
    PRINT '  V2_RBILL_Bill restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 3. V2_RBILL_BillLine (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_BillLine'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_BillLine...'
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
    PRINT '  V2_RBILL_BillLine restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 4. V2_RBILL_BillGl
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_BillGl'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_BillGl...'
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
    PRINT '  V2_RBILL_BillGl restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 5. V2_RBILL_BillReading
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_BillReading'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_BillReading...'
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
    PRINT '  V2_RBILL_BillReading restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 6. V2_RBILL_BillAdjustment
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_BillAdjustment'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_BillAdjustment...'
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
    PRINT '  V2_RBILL_BillAdjustment restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 7. V2_RBILL_BillCfdSettlement
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_BillCfdSettlement'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_BillCfdSettlement...'
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
    PRINT '  V2_RBILL_BillCfdSettlement restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 8. V2_RBILL_InvoiceReversal
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceReversal'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoiceReversal...'
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
    PRINT '  V2_RBILL_InvoiceReversal restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 9. V2_RBILL_InvoiceStatusHistory
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceStatusHistory'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoiceStatusHistory...'
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
    PRINT '  V2_RBILL_InvoiceStatusHistory restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 10. V2_RBILL_InvoiceEvent
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceEvent'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoiceEvent...'
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
    PRINT '  V2_RBILL_InvoiceEvent restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 11. V2_RBILL_InvoiceChargeCategory
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceChargeCategory'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoiceChargeCategory...'
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
    PRINT '  V2_RBILL_InvoiceChargeCategory restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 12. V2_RBILL_InvoiceDunning (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceDunning'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoiceDunning...'
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
    PRINT '  V2_RBILL_InvoiceDunning restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 13. V2_RBILL_InvoiceOutput
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceOutput'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoiceOutput...'
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
    PRINT '  V2_RBILL_InvoiceOutput restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 14. V2_RBILL_InvoiceFileVault
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceFileVault'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoiceFileVault...'
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
    PRINT '  V2_RBILL_InvoiceFileVault restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 15. V2_RBILL_InvoicePDFFileVault
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePDFFileVault'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoicePDFFileVault...'
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
    PRINT '  V2_RBILL_InvoicePDFFileVault restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 16. V2_RBILL_InvoicePrintAudit
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintAudit'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoicePrintAudit...'
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
    PRINT '  V2_RBILL_InvoicePrintAudit restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 17. V2_RBILL_InvoicePrintStatusCurrent (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintStatusCurrent'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoicePrintStatusCurrent...'
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
    PRINT '  V2_RBILL_InvoicePrintStatusCurrent restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 18. V2_RBILL_InvoicePrintStatusHistory (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintStatusHistory'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_InvoicePrintStatusHistory...'
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
    PRINT '  V2_RBILL_InvoicePrintStatusHistory restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 19. V2_RBILL_StatementInvoice
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_StatementInvoice'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_StatementInvoice...'
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
    PRINT '  V2_RBILL_StatementInvoice restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 20. V2_RBILL_Transaction (exclude Version - timestamp, exclude IsStatement - computed)
--     Insert WITHOUT self-referencing columns first, then UPDATE them
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_Transaction'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_Transaction...'

    -- Disable triggers on Transaction to prevent RBILL_AccountBalance trigger
    -- from firing during bulk restore (it fails with subquery error on multi-row insert)
    EXEC('DISABLE TRIGGER ALL ON dbo.V2_RBILL_Transaction')
    PRINT '  Disabled triggers on V2_RBILL_Transaction'

    -- Step 1: Insert rows with self-refs set to NULL to avoid circular FK issues
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
    PRINT '  V2_RBILL_Transaction inserted: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

    -- Step 2: Now update the self-referencing columns from backup
    SET @SQL = '
    UPDATE t
    SET t.ReversedTransactionId = bk.ReversedTransactionId,
        t.AssociatedTransactionId = bk.AssociatedTransactionId
    FROM dbo.V2_RBILL_Transaction t
    INNER JOIN dbo.' + @FullPrefix + 'V2_RBILL_Transaction bk ON bk.TransactionId = t.TransactionId
    WHERE t.AccountId = ' + CAST(@AccountId AS NVARCHAR(20)) + '
      AND (bk.ReversedTransactionId IS NOT NULL OR bk.AssociatedTransactionId IS NOT NULL)'
    EXEC sp_executesql @SQL
    PRINT '  V2_RBILL_Transaction self-refs updated: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'

    -- Re-enable triggers
    EXEC('ENABLE TRIGGER ALL ON dbo.V2_RBILL_Transaction')
    PRINT '  Re-enabled triggers on V2_RBILL_Transaction'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 21. V2_RBILL_AccountBalance (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_AccountBalance'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_AccountBalance...'
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
    PRINT '  V2_RBILL_AccountBalance restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 22. V2_RBILL_AccountBalanceCurrent (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_AccountBalanceCurrent'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_AccountBalanceCurrent...'
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
    PRINT '  V2_RBILL_AccountBalanceCurrent restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 23. V2_RBILL_Allocation (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_Allocation'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_Allocation...'
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
    PRINT '  V2_RBILL_Allocation restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


-- ============================================================
-- 24. V2_RBILL_AllocationDetail (exclude Version - timestamp)
-- ============================================================
SET @TableName = @FullPrefix + 'V2_RBILL_AllocationDetail'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN
    PRINT 'Restoring V2_RBILL_AllocationDetail...'
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
    PRINT '  V2_RBILL_AllocationDetail restored: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + ' rows'
END
ELSE
    PRINT 'SKIPPED: Backup table ' + @TableName + ' not found.'


PRINT ''
PRINT '========================================'
PRINT 'Restore complete for AccountId: ' + CAST(@AccountId AS NVARCHAR(20))
PRINT 'Full prefix used: ' + @FullPrefix
PRINT '========================================'