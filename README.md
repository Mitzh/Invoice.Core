# Mitzh.Invoice.Core

[![Build](https://github.com/Mitzh/Invoice.Core/actions/workflows/ci.yml/badge.svg)](https://github.com/Mitzh/Invoice.Core/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Mitzh.Invoice.Core.svg)](https://www.nuget.org/packages/Mitzh.Invoice.Core)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Powered by Codex](https://img.shields.io/badge/Powered%20by-Codex-412991)](https://openai.com/codex/)

> 本项目由 **Codex 驱动支持**，使用 Codex 辅助完成开发、重构、测试和文档维护。

代码命名空间：`Mitzh.Invoice.Core`。本项目拥有独立目录、解决方案、Git 仓库和发布流程。

.NET 10 离线发票解析类库，根命名空间 `Mitzh.Invoice.Core`。支持文件路径、文件流及 XML 内容，直接返回 `InvoiceModel`。

## 安装

```powershell
dotnet add package Mitzh.Invoice.Core --version 3.0.0
```

## 文件路径、流及 XML 内容

```csharp
using Mitzh.Invoice.Core;

using var recognizer = new InvoiceRecognizer(@"D:\InvoiceParser\models");
InvoiceModel fromPath = await recognizer.RecognizeAsync(@"D:\invoices\sample.ofd");

await using var stream = File.OpenRead(@"D:\invoices\sample.pdf");
InvoiceModel fromStream = await recognizer.RecognizeAsync(stream);

string xml = await File.ReadAllTextAsync(@"D:\invoices\sample.xml");
InvoiceModel fromXml = await recognizer.RecognizeXmlAsync(xml);
```

XML、原生文字 PDF/OFD 不需要模型，可以使用 `new InvoiceRecognizer()`。图片/扫描 PDF 需要指定本地中文 OCR 模型目录；默认目录为应用可执行文件旁的 `models/`。Core NuGet 包不包含模型，使用 [仓库 models](https://github.com/Mitzh/Invoice.Core/tree/main/models) 的模型及配套字典，保留第三方许可证。模型加载后常驻，建议复用一个识别器；内部限制并发，不要在调用完成前 Dispose。输入流保持打开，不要求支持 Seek。

扫描 PDF 另需本地 `pdftoppm`，可用 `InvoiceRecognitionOptions.PdfToPpm` 指定完整路径；不使用外部网络 OCR 服务。

```csharp
ParseResult detailed = await recognizer.RecognizeDetailedAsync(@"D:\invoices\sample.ofd");
IReadOnlyList<TextBox> boxes = detailed.Evidence;
IReadOnlyList<StructuredFieldEvidence> fields = detailed.StructuredFields;
```

`StructuredFields` 仅结构化 XML/OFD 附件返回，可为 null；`Evidence` 为文字坐标证据。不能将结构化 XML 伪造为 OCR 坐标。

## 模型

- 发票：`Type`、`Code`、`Number`、`IssueDate`、`CheckCode`、`Drawer`。
- 购销方：`BuyerName/BuyerTaxId`、`SellerName/SellerTaxId`。
- 金额：`Amount`（不含税）、`Tax`、`Total`（价税合计），使用 decimal?。
- `Items`：商品、规格、单位、数量、单价、金额、税率、税额、页码。
- `RailwayTicket`：铁路客票乘车人、脱敏证件号、车次、区间、乘车日期/时间、席别、车厢/座号、电子客票号、业务类型；普通发票为 null。
- `Warnings` 与 `RequiresReview`：缺项、冲突、金额一致性及真实性未验证提示。警告不等同于解析异常。

未识别字段保持 null，发票号码保持字符串及前导零。类库异常通过正常 .NET 异常机制抛出；HTTP 的 code/msg/data 包装属于 Api 包。

## 能力边界

支持 EInvoice 数电 XML、原生文字 OFD/PDF、JPG/PNG 及部分扫描 PDF 版式；铁路 OFD 要求内嵌 rai 2021-11-30 XBRL 附件，单文档/单上下文。未知版式、图片 OFD、复杂图形变换可能需要人工复核或渲染器。

默认 20 MiB、30 页、120 秒超时，可用 `InvoiceRecognitionOptions` 调整。XML 禁止 DTD/外部实体，不读取外部 schema。**未验证数字签名或税务真实性。**

源码、完整说明、许可和回归程序：[Mitzh/Invoice.Core](https://github.com/Mitzh/Invoice.Core)。

## 独立构建与验证

```powershell
dotnet restore Invoice.Core.sln --locked-mode
dotnet build Invoice.Core.sln -c Release --no-restore
./tests/RunChecks.ps1
dotnet pack src/Invoice.Core -c Release -o artifacts
```

HTTP 服务见独立仓库 [Mitzh/Invoice.Api](https://github.com/Mitzh/Invoice.Api)。

## 版本迁移

2.0.0 将原 `using Mitzh;` 拆分为各项目专属命名空间。类库调用者改用 `using Mitzh.Invoice.Core;`；API 类型位于 `Mitzh.Invoice.Api`。现有 HTTP 路由和 JSON 字段保持兼容。1.0.0 已发布版本不会被覆盖。

## .NET 10 升级（3.0.0）

3.0.0 仅面向 `net10.0`，需要 .NET 10 SDK / Runtime；Api 需要 ASP.NET Core 10 Runtime。原有命名空间、调用签名、HTTP 路由和 JSON 返回格式保持不变。Api 通过 NuGet 引用 Core 3.0.0，两个仓库继续独立构建。`global.json` 限定使用 .NET 10 SDK。需要继续使用 .NET 8 的应用可保留 2.x，不应直接升级到 3.x。
