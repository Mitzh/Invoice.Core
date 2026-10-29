# 铁路电子客票 OFD

现有 RecognizeAsync(path)、RecognizeAsync(stream) 和 HTTP 上传接口均支持包含财政部 2021-11-30 rai 分类标准 XBRL 附件的铁路电子客票 OFD，无需 OCR 模型。

通过 OFD 文档及附件清单定位 XML，读取发票号码、开票日期、购买方、金额、税额、票价及备注。新增可空 `railwayTicket` 对象：`passengerName`、`idNumber`、`trainNumber`、`departureStation`、`destinationStation`、`travelDate`、`departureTime`、`seatLevel`、`carriage`、`seat`、`eTicketNumber`、`businessType`。证件号原样保留脱敏字符；乘车日期与开票日期分别返回。乘车人不会写入购买方。

附件中未提供的销售方、开票人等信息返回 null。铁路交通信息放在 railwayTicket，Items 不生成虚构商品明细。当前仅支持单文档、单铁路附件及单 XBRL 上下文；重复字段、多上下文和多铁路附件明确拒绝。无该附件的 OFD 继续使用原有版式文字解析，不保证能还原铁路专属字段。

XML 禁用 DTD 和外部资源解析，限制归档、XML 大小和深度，不访问外部分类标准链接。此功能仅解析内容，未验证数字签名或税务真实性，RequiresReview 保持 true。

验证：109 项检查通过，包含合成铁路客票的身份/日期/金额映射、路径与流一致性、流保持打开、脱敏保留、重复字段和混合上下文拒绝。另用用户提供的原件完成路径、流及 HTTP 验证。真实客票不包含在源码包中。
