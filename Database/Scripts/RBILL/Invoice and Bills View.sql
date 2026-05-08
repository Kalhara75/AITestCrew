
declare @AccountId int = 335568

select * from dbo.V2_RBILL_Invoice
WHERE AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_Bill
WHERE AccountId = @AccountId


SELECT * FROM dbo.V2_RBILL_BillLine bl
inner join dbo.V2_RBILL_Bill b ON b.BillId = bl.BillId
WHERE b.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_BillGl bg
inner join dbo.V2_RBILL_Bill b ON b.BillId = bg.BillId
WHERE b.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_BillReading br
inner join dbo.V2_RBILL_Bill b ON b.BillId = br.BillId
WHERE b.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_BillAdjustment ba
inner join dbo.V2_RBILL_Bill b ON b.BillId = ba.OriginalBillId
WHERE b.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_BillCfdSettlement cd
inner join dbo.V2_RBILL_Bill b ON b.BillId = cd.BillId
WHERE b.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoiceReversal ir
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = ir.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoiceStatusHistory ih
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = ih.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoiceEvent ie
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = ie.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoiceChargeCategory ic
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = ic.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoiceDunning d
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = d.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoiceOutput iot
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = iot.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoiceFileVault ifv
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = ifv.InvoiceId
WHERE i.AccountId = @AccountId

SELECT pdf.* FROM dbo.V2_RBILL_InvoicePDFFileVault pdf
INNER JOIN dbo.V2_RBILL_InvoiceFileVault fv ON pdf.InvoiceFileVaultId = fv.InvoiceFileVaultId
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = fv.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoicePrintAudit ipa
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = ipa.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoicePrintStatusCurrent ps
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = ps.InvoiceId
WHERE i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_InvoicePrintStatusHistory sh
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = sh.InvoiceId
WHERE i.AccountId = @AccountId


SELECT * FROM dbo.V2_RBILL_StatementInvoice s
inner join dbo.V2_RBILL_Invoice i ON i.InvoiceId = s.InvoiceId
where i.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_Transaction t
inner join dbo.V2_RBILL_Invoice i ON t.AccountId = i.AccountId
WHERE t.AccountId = @AccountId
  

SELECT ab.* FROM dbo.V2_RBILL_AccountBalance ab
WHERE AccountId = @AccountId

SELECT abc.* FROM dbo.V2_RBILL_AccountBalanceCurrent abc
where AccountId = @AccountId

SELECT ad.* FROM dbo.V2_RBILL_AllocationDetail ad
INNER JOIN dbo.V2_RBILL_Allocation a ON a.AllocationId = ad.AllocationId
WHERE a.AccountId = @AccountId

SELECT * FROM dbo.V2_RBILL_Allocation
WHERE AccountId = @AccountId