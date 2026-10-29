# 第三方组件与模型来源

交付需保留第三方许可证，生产方应结合实际部署包核对原生库、传递依赖和模型许可证。

| 组件 | 版本/来源 | 上游许可声明 |
|---|---|---|
| RapidOcrNet | 4.2.0 / https://github.com/BobLd/RapidOcrNet | Apache-2.0 |
| Swashbuckle.AspNetCore | 6.9.0 / https://github.com/domaindrivendev/Swashbuckle.AspNetCore | MIT；Swagger UI 及其资源按上游 notices |
| PdfPig | 0.1.14 / https://github.com/UglyToad/PdfPig | Apache-2.0 |
| ONNX Runtime | 传递依赖，见 packages.lock.json / https://github.com/microsoft/onnxruntime | MIT |
| SkiaSharp | 传递依赖，见 packages.lock.json / https://github.com/mono/SkiaSharp | MIT 及 native third-party notices |
| Poppler | 外部可执行文件，未随源码包分发 / https://poppler.freedesktop.org/ | GPL 系列，依发行包核对 |

中文模型清单由 RapidAI 发布：https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml 。RapidOCR 与 PaddleOCR 仓库采用 Apache-2.0；部署前应复核模型发布页及其来源条款。

- 检测与分类模型：来自 RapidOcrNet 4.2.0 NuGet 的 models/v5，哈希见 sha256.json。
- 中文识别模型：https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx
- 配套字典：https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt
- 中文识别模型 SHA256：5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5，已与上游清单核对。
- 字典 SHA256：d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b，为本次下载计算值。

测试程序使用机器本地字体生成合成图片；不分发 Windows 字体文件。
