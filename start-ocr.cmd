@echo off
setlocal
cd /d "%~dp0"
if not defined DP_LABEL_REC_MODEL (
  set "DP_LABEL_REC_MODEL=%~dp0models\rec.onnx"
)
if not exist "%DP_LABEL_REC_MODEL%" (
  echo Recognition model missing. Set DP_LABEL_REC_MODEL to a trusted PP-OCRv4 recognition ONNX file.
  pause
  exit /b 1
)
call start.cmd %*
endlocal
