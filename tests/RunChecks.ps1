param([string]$Fixtures = (Join-Path $env:TEMP 'invoice-center-checks'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:TEST_OCR_DET = Join-Path $projectRoot 'models/ch_PP-OCRv5_mobile_det.onnx'
$env:TEST_OCR_CLS = Join-Path $projectRoot 'models/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'
$env:TEST_OCR_REC = Join-Path $projectRoot 'models/ch_PP-OCRv5_rec_mobile.onnx'
$env:TEST_OCR_DICT = Join-Path $projectRoot 'models/ppocrv5_dict.txt'
dotnet run --project (Join-Path $PSScriptRoot 'Invoice.Checks') -- $Fixtures
exit $LASTEXITCODE
