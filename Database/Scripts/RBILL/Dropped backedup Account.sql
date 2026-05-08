-- ============================================================
-- Drop All Backup Tables
-- ============================================================
-- Set your parameters here (must match the values used during backup):
DECLARE @AccountId INT = 106
DECLARE @Prefix NVARCHAR(50) = 'BKP_'
-- ============================================================
-- The full prefix will be: @Prefix + @AccountId + '_'
-- Example: BKP_317774_V2_RBILL_Invoice
-- ============================================================

DECLARE @SQL NVARCHAR(MAX)
DECLARE @TableName NVARCHAR(200)
DECLARE @FullPrefix NVARCHAR(100) = @Prefix + CAST(@AccountId AS NVARCHAR(20)) + '_'
DECLARE @DroppedCount INT = 0
DECLARE @SkippedCount INT = 0

PRINT '========================================'
PRINT 'Dropping backup tables with prefix: ' + @FullPrefix
PRINT '========================================'

-- 1. V2_RBILL_InvoicePDFFileVault
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePDFFileVault'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 2. V2_RBILL_InvoiceFileVault
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceFileVault'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 3. V2_RBILL_InvoicePrintAudit
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintAudit'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 4. V2_RBILL_InvoicePrintStatusCurrent
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintStatusCurrent'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 5. V2_RBILL_InvoicePrintStatusHistory
SET @TableName = @FullPrefix + 'V2_RBILL_InvoicePrintStatusHistory'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 6. V2_RBILL_InvoiceReversal
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceReversal'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 7. V2_RBILL_InvoiceStatusHistory
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceStatusHistory'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 8. V2_RBILL_InvoiceEvent
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceEvent'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 9. V2_RBILL_InvoiceChargeCategory
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceChargeCategory'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 10. V2_RBILL_InvoiceDunning
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceDunning'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 11. V2_RBILL_InvoiceOutput
SET @TableName = @FullPrefix + 'V2_RBILL_InvoiceOutput'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 12. V2_RBILL_StatementInvoice
SET @TableName = @FullPrefix + 'V2_RBILL_StatementInvoice'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 13. V2_RBILL_BillAdjustment
SET @TableName = @FullPrefix + 'V2_RBILL_BillAdjustment'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 14. V2_RBILL_BillLine
SET @TableName = @FullPrefix + 'V2_RBILL_BillLine'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 15. V2_RBILL_BillGl
SET @TableName = @FullPrefix + 'V2_RBILL_BillGl'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 16. V2_RBILL_BillReading
SET @TableName = @FullPrefix + 'V2_RBILL_BillReading'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 17. V2_RBILL_BillCfdSettlement
SET @TableName = @FullPrefix + 'V2_RBILL_BillCfdSettlement'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 18. V2_RBILL_Bill
SET @TableName = @FullPrefix + 'V2_RBILL_Bill'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 19. V2_RBILL_AllocationDetail
SET @TableName = @FullPrefix + 'V2_RBILL_AllocationDetail'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 20. V2_RBILL_Allocation
SET @TableName = @FullPrefix + 'V2_RBILL_Allocation'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 21. V2_RBILL_AccountBalance
SET @TableName = @FullPrefix + 'V2_RBILL_AccountBalance'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 22. V2_RBILL_AccountBalanceCurrent
SET @TableName = @FullPrefix + 'V2_RBILL_AccountBalanceCurrent'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 23. V2_RBILL_Transaction
SET @TableName = @FullPrefix + 'V2_RBILL_Transaction'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 24. V2_RBILL_Invoice
SET @TableName = @FullPrefix + 'V2_RBILL_Invoice'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 25.V2_RBILL_RescheduledBill
SET @TableName = @FullPrefix + 'V2_RBILL_RescheduledBill'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 26. V2_RBILL_CollectionPrintStatusCurrent
SET @TableName = @FullPrefix + 'V2_RBILL_CollectionPrintStatusCurrent'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

-- 27. V2_RBILL_CollectionPrintStatusHistory
SET @TableName = @FullPrefix + 'V2_RBILL_CollectionPrintStatusHistory'
IF OBJECT_ID('dbo.' + @TableName, 'U') IS NOT NULL
BEGIN SET @SQL = 'DROP TABLE dbo.' + @TableName EXEC sp_executesql @SQL PRINT '  Dropped ' + @TableName SET @DroppedCount = @DroppedCount + 1 END
ELSE BEGIN PRINT '  Skipped ' + @TableName + ' (not found)' SET @SkippedCount = @SkippedCount + 1 END

PRINT ''
PRINT '========================================'
PRINT 'Cleanup complete for AccountId: ' + CAST(@AccountId AS NVARCHAR(20))
PRINT 'Full prefix used: ' + @FullPrefix
PRINT 'Tables dropped: ' + CAST(@DroppedCount AS NVARCHAR(10))
PRINT 'Tables not found (skipped): ' + CAST(@SkippedCount AS NVARCHAR(10))
PRINT '========================================'