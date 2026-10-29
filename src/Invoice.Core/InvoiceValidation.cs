namespace Mitzh.Invoice.Core;

internal static class InvoiceValidation
{
    public static void Check(InvoiceModel invoice, List<string> issues)
    {
        if (invoice.Number == null)
        {
            issues.Add("未提取到合法发票号码。");
        }

        if (invoice.Number?.Length == 8 && invoice.Code == null)
        {
            issues.Add("传统发票缺少发票代码。");
        }

        if (invoice.IssueDate == null)
        {
            issues.Add("缺少有效开票日期。");
        }

        if (invoice.BuyerName == null || invoice.SellerName == null || invoice.BuyerTaxId == null || invoice.SellerTaxId == null)
        {
            issues.Add("购销方信息不完整或版式未适配。");
        }

        if (invoice.Amount == null || invoice.Tax == null || invoice.Total == null)
        {
            issues.Add("金额汇总不完整。");
        }

        if (invoice.Items.Count == 0)
        {
            issues.Add("未还原明细，需要人工复核。");
        }

        try
        {
            if (invoice.Amount != null && invoice.Tax != null && invoice.Total != null && Math.Abs(invoice.Amount.Value + invoice.Tax.Value - invoice.Total.Value) > .01m)
            {
                issues.Add("金额加税额与价税合计不一致。");
            }

            if (invoice.Items.Count > 0 && invoice.Amount != null && Math.Abs(invoice.Items.Sum(i => i.Amount ?? 0) - invoice.Amount.Value) > .01m)
            {
                issues.Add("明细金额与汇总不一致。");
            }

            if (invoice.Items.Any(i => i.Quantity != null && i.UnitPrice != null && i.Amount != null && Math.Abs(decimal.Round(i.Quantity.Value * i.UnitPrice.Value, 2, MidpointRounding.AwayFromZero) - i.Amount.Value) > .01m))
            {
                issues.Add("明细数量乘单价与金额不一致。");
            }

            if (invoice.Items.Count > 0 && invoice.Tax != null && invoice.Items.All(i => i.Tax != null) && Math.Abs(invoice.Items.Sum(i => i.Tax.Value) - invoice.Tax.Value) > .01m)
            {
                issues.Add("明细税额与汇总不一致。");
            }
        }
        catch (OverflowException)
        {
            issues.Add("金额或数量计算超出 decimal 范围，需复核。");
        }
    }
}
