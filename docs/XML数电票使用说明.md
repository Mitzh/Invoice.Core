# XML 数电票解析

2026-10-26。新增 `EInvoice` 通用结构适配，复用 InvoiceModel，文件或内容直接解析，不经过 OCR，无需 OCR 模型或 Poppler。

## 极简调用

```csharp
using Mitzh.Invoice.Core;

using var recognizer = new InvoiceRecognizer();

InvoiceModel fromFile = await recognizer.RecognizeAsync(@"D:\invoices\invoice.xml");

using Stream stream = File.OpenRead(@"D:\invoices\invoice.xml");
InvoiceModel fromStream = await recognizer.RecognizeAsync(stream);

string xmlContent = await File.ReadAllTextAsync(@"D:\invoices\invoice.xml");
InvoiceModel fromContent = await recognizer.RecognizeXmlAsync(xmlContent);
```

传入的 Stream 从当前位置读取、无需 Seek，保持打开。路径方法与此前文件调用兼容，按内容识别格式。`RecognizeAsync(string)` 的字符串始终是路径；XML 原文请使用明确的 `RecognizeXmlAsync(string)`，避免含糊判断。

已解码的 XML 字符串通过 StringReader 读取，声明里的字节编码不会将字符串二次错误转码。文件/流由 XmlReader 按声明及 BOM 解码，测试覆盖 UTF-8、UTF-16 LE/BE、UTF-32 LE/BE、GB18030。

## HTTP 与 Swagger

- 原件上传：`POST /api/invoices/recognize`，multipart 的 `file` 选择 `.xml`，成功返回统一响应，data 为 InvoiceModel。
- XML 原文：同一接口使用 `Content-Type: application/xml`，请求体就是完整 XML 文本。
- JSON 内容：`POST /api/invoices/recognize/xml`，请求体 `{ "xml": "<EInvoice>...</EInvoice>" }`。
- 后台入口：`POST /api/invoices` 同样可上传 `.xml`，返回 202 与任务 id，再查询 `/api/jobs/{id}`。

所有业务入口沿用 `X-Api-Key`。Swagger 中新增 XML 内容接口及可直接执行的合成示例；文件接口增加 application/xml 选项。`samples/einvoice.xml` 为合成测试文件，不是签发原件，不能用于报销。

```powershell
$headers = @{ 'X-Api-Key' = $env:Limits__ApiKey }
$xml = Get-Content -Raw -Encoding utf8 ./samples/einvoice.xml
Invoke-RestMethod 'http://127.0.0.1:5088/api/invoices/recognize/xml' `
    -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes((@{xml=$xml}|ConvertTo-Json)))
```

XML 内容为空返回 400；非法 XML、DTD、未知结构或结构限制超限返回 422；内容尺寸超限可返回 413；未配置密钥/密钥错误沿用既有行为。XML 解析不返回 OCR_NOT_CONFIGURED。

## 已适配字段

| 模型 | XML 路径（相对 EInvoice） |
|---|---|
| Type | Header/InherentLabel/EInvoiceType/LabelName + GeneralOrSpecialVAT/LabelName |
| Number | TaxSupervisionInfo/InvoiceNumber，必须为 20 位数字，保留前导零 |
| IssueDate | TaxSupervisionInfo/IssueTime，日期或支持的 ISO 时间戳 |
| Code / CheckCode | TaxSupervisionInfo/InvoiceCode、CheckCode（可空） |
| BuyerName / BuyerTaxId | EInvoiceData/BuyerInformation/BuyerName、BuyerIdNum |
| SellerName / SellerTaxId | EInvoiceData/SellerInformation/SellerName、SellerIdNum |
| Amount | EInvoiceData/BasicInformation/TotalAmWithoutTax |
| Tax | EInvoiceData/BasicInformation/TotalTaxAm |
| Total | EInvoiceData/BasicInformation/TotalTax-includedAmount |
| Remarks | EInvoiceData/AdditionalInformation/Remark |
| Drawer | EInvoiceData/BasicInformation/Drawer（兼容 DrawerName） |
| Items | EInvoiceData 下重复的 IssuItemInformation 节点，保留顺序 |

每个明细对应：ItemName、SpecMod、MeaUnits、Quantity、UnPrice、Amount、TaxRate、ComTaxAm。数字税率 0.06 输出为 6%，文字税率（如免税）保留原值；数量与单价使用 decimal，不按显示小数位截断。数值无法无损表示为 decimal 时留空并提示，不静默四舍五入。负数金额保留，但不据此断言红冲验真状态。

兼容的明确字段别名包括 BuyerTaxId、SellerTaxId、TotalAmountWithoutTax、TotalAmtWithoutTax、TotalTaxAmount、TotalTaxIncludedAmount，以及明细 Specification、Unit、UnitPrice、TaxAmount、IssueItemInformation。重复同名/别名字段有冲突时不自动选择；重复主体或金额区块直接拒绝。

按直接父子关系和所在发票命名空间读取，支持默认命名空间及前缀；不遍历整篇寻找同名 Amount 或 Name，避免将明细、原蓝票、签名或外来命名空间字段混入主表。

`Header/EIid` 不自动代替发票号码，`BasicInformation/RequestTime` 不代替税务开票时间。未知字段不会按 OCR 关键词猜测。

## 检查与证据

复用金额+税额=总额、明细金额/税额合计、数量×单价检查，差值容忍度 0.01 元。空字段为 null，0 仍为 0；异常数值保留原值到详细证据并添加 Warnings。

`RecognizeXmlDetailedAsync(xmlContent)` 或原有 `RecognizeDetailedAsync(path/stream)` 返回 StructuredFields：每个已映射字段的路径与原始文本。XML 没有版式坐标，Evidence 为 []，明细 Page 为 0（不适用），不伪造页码或 OCR 置信度。

## 安全与边界

- 仅适配上述 EInvoice 通用结构；传统 Invoice XML、XBRL、任意 ERP 导出 XML、多票包装 XML 不按该规则猜读。行业 SpecificInformation 有内容时提示未完整映射。
- 禁止 DTD、外部实体及网络资源解析。限制总字符数、深度 64、节点 100000、明细 10000、已映射单字段 65536 字符、数值字段 128 字符；请求/文件默认仍为 20 MiB。
- 支持格式解析，不做完整 XSD 合规认证或 XML 数字签名校验；无论是否包含 SignatureValue，均提示签名及税务验真未校验。
- 不改写原件，也不新增数据库和对象存储。上传文件按既有临时文件生命周期处理，不作为档案库。
- 当前未提供用户真实 XML 原件，已用合成票样验证结构、编码及异常分支；实际开票平台版本和行业扩展仍需真实样本验收。

税务部门将 XML 与 PDF/OFD 作为不同交付格式说明，XML 的解析与签名/归档要求不能混为一谈。参考：[税务总局政策解读](https://fgk.chinatax.gov.cn/zcfgk/c100015/c5236070/content.html)、[财政部电子凭证数据标准应用资料](https://ah.mof.gov.cn/tongzhitonggao/202506/t20250618_3966020.htm)。本适配器的字段清单与测试样例不是税务机关的完整 XSD。
