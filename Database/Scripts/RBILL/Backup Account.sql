-- ============================================================
-- Backup Account Data Script
-- ============================================================
-- Set your parameters here:
DECLARE @AccountId INT = 106
DECLARE @Prefix NVARCHAR(50) = 'BKP_'  -- Change this to your desired prefix
-- ============================================================
-- The final table prefix will be: @Prefix + @AccountId + '_'
-- Example: BKP_317774_V2_RBILL_Invoice
-- ============================================================

DECLARE @SQL NVARCHAR(MAX)
DECLARE @FullPrefix NVARCHAR(100) = @Prefix + CAST(@AccountId AS NVARCHAR(20)) + '_'

-- ============================================================
-- 1. V2_RBILL_Invoice
-- ============================================================
SET @SQL = '
SELECT *
INTO dbo.' + @FullPrefix + 'V2_RBILL_Invoice
FROM dbo.V2_RBILL_Invoice
WHERE AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 2. V2_RBILL_Bill
-- ============================================================
SET @SQL = '
SELECT *
INTO dbo.' + @FullPrefix + 'V2_RBILL_Bill
FROM dbo.V2_RBILL_Bill
WHERE AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 3. V2_RBILL_BillLine
-- ============================================================
SET @SQL = '
SELECT bl.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_BillLine
FROM dbo.V2_RBILL_BillLine bl
INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = bl.BillId
WHERE b.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 4. V2_RBILL_BillGl
-- ============================================================
SET @SQL = '
SELECT bg.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_BillGl
FROM dbo.V2_RBILL_BillGl bg
INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = bg.BillId
WHERE b.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 5. V2_RBILL_BillReading
-- ============================================================
SET @SQL = '
SELECT br.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_BillReading
FROM dbo.V2_RBILL_BillReading br
INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = br.BillId
WHERE b.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 6. V2_RBILL_BillAdjustment
-- ============================================================
SET @SQL = '
SELECT ba.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_BillAdjustment
FROM dbo.V2_RBILL_BillAdjustment ba
INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = ba.OriginalBillId
WHERE b.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 7. V2_RBILL_BillCfdSettlement
-- ============================================================
SET @SQL = '
SELECT cd.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_BillCfdSettlement
FROM dbo.V2_RBILL_BillCfdSettlement cd
INNER JOIN dbo.V2_RBILL_Bill b ON b.BillId = cd.BillId
WHERE b.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 8. V2_RBILL_InvoiceReversal
-- ============================================================
SET @SQL = '
SELECT ir.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoiceReversal
FROM dbo.V2_RBILL_InvoiceReversal ir
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ir.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 9. V2_RBILL_InvoiceStatusHistory
-- ============================================================
SET @SQL = '
SELECT ih.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoiceStatusHistory
FROM dbo.V2_RBILL_InvoiceStatusHistory ih
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ih.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 10. V2_RBILL_InvoiceEvent
-- ============================================================
SET @SQL = '
SELECT ie.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoiceEvent
FROM dbo.V2_RBILL_InvoiceEvent ie
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ie.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 11. V2_RBILL_InvoiceChargeCategory
-- ============================================================
SET @SQL = '
SELECT ic.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoiceChargeCategory
FROM dbo.V2_RBILL_InvoiceChargeCategory ic
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ic.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 12. V2_RBILL_InvoiceDunning
-- ============================================================
SET @SQL = '
SELECT d.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoiceDunning
FROM dbo.V2_RBILL_InvoiceDunning d
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = d.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 13. V2_RBILL_InvoiceOutput
-- ============================================================
SET @SQL = '
SELECT iot.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoiceOutput
FROM dbo.V2_RBILL_InvoiceOutput iot
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = iot.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 14. V2_RBILL_InvoiceFileVault
-- ============================================================
SET @SQL = '
SELECT ifv.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoiceFileVault
FROM dbo.V2_RBILL_InvoiceFileVault ifv
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ifv.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 15. V2_RBILL_InvoicePDFFileVault
-- ============================================================
SET @SQL = '
SELECT pdf.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoicePDFFileVault
FROM dbo.V2_RBILL_InvoicePDFFileVault pdf
INNER JOIN dbo.V2_RBILL_InvoiceFileVault fv ON pdf.InvoiceFileVaultId = fv.InvoiceFileVaultId
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = fv.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 16. V2_RBILL_InvoicePrintAudit
-- ============================================================
SET @SQL = '
SELECT ipa.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoicePrintAudit
FROM dbo.V2_RBILL_InvoicePrintAudit ipa
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ipa.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 17. V2_RBILL_InvoicePrintStatusCurrent
-- ============================================================
SET @SQL = '
SELECT ps.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoicePrintStatusCurrent
FROM dbo.V2_RBILL_InvoicePrintStatusCurrent ps
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ps.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 18. V2_RBILL_InvoicePrintStatusHistory
-- ============================================================
SET @SQL = '
SELECT sh.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_InvoicePrintStatusHistory
FROM dbo.V2_RBILL_InvoicePrintStatusHistory sh
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = sh.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 19. V2_RBILL_StatementInvoice
-- ============================================================
SET @SQL = '
SELECT s.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_StatementInvoice
FROM dbo.V2_RBILL_StatementInvoice s
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = s.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 20. V2_RBILL_Transaction
-- ============================================================
SET @SQL = '
SELECT t.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_Transaction
FROM dbo.V2_RBILL_Transaction t
WHERE t.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 21. V2_RBILL_AccountBalance
-- ============================================================
SET @SQL = '
SELECT ab.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_AccountBalance
FROM dbo.V2_RBILL_AccountBalance ab
WHERE ab.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 22. V2_RBILL_AccountBalanceCurrent
-- ============================================================
SET @SQL = '
SELECT abc.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_AccountBalanceCurrent
FROM dbo.V2_RBILL_AccountBalanceCurrent abc
WHERE abc.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 23. V2_RBILL_AllocationDetail
-- ============================================================
SET @SQL = '
SELECT ad.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_AllocationDetail
FROM dbo.V2_RBILL_AllocationDetail ad
INNER JOIN dbo.V2_RBILL_Allocation a ON a.AllocationId = ad.AllocationId
WHERE a.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL

-- ============================================================
-- 24. V2_RBILL_Allocation
-- ============================================================
SET @SQL = '
SELECT *
INTO dbo.' + @FullPrefix + 'V2_RBILL_Allocation
FROM dbo.V2_RBILL_Allocation
WHERE AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL


-- ============================================================
-- 25. V2_RBILL_RescheduledBill
-- ============================================================
SET @SQL = '
SELECT bl.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_RescheduledBill
FROM dbo.V2_RBILL_RescheduledBill bl
INNER JOIN dbo.V2_RBILL_Bill b ON bl.RescheduledBillId = b.BillId or bl.OriginalBillId = b.BillId
WHERE b.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL


-- ============================================================
-- 26. V2_RBILL_CollectionPrintStatusCurrent
-- ============================================================
SET @SQL = '
SELECT ir.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_CollectionPrintStatusCurrent
FROM dbo.V2_RBILL_CollectionPrintStatusCurrent ir
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ir.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL


-- ============================================================
-- 27. V2_RBILL_CollectionPrintStatusHistory
-- ============================================================
SET @SQL = '
SELECT ir.*
INTO dbo.' + @FullPrefix + 'V2_RBILL_CollectionPrintStatusHistory
FROM dbo.V2_RBILL_CollectionPrintStatusHistory ir
INNER JOIN dbo.V2_RBILL_Invoice i ON i.InvoiceId = ir.InvoiceId
WHERE i.AccountId = ' + CAST(@AccountId AS NVARCHAR(20))
EXEC sp_executesql @SQL



PRINT '========================================'
PRINT 'Backup complete for AccountId: ' + CAST(@AccountId AS NVARCHAR(20))
PRINT 'Table prefix used: ' + @FullPrefix
PRINT 'Example table: dbo.' + @FullPrefix + 'V2_RBILL_Invoice'
PRINT '24 tables backed up successfully.'
PRINT '========================================'