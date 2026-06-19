# Downloads the local embedding model + tokenizer for engram.
# all-MiniLM-L6-v2 (384-dim sentence embeddings), ONNX export.
# These are gitignored (the model is ~90MB); run this once after cloning.

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$models = Join-Path $PSScriptRoot "..\src\models"
New-Item -ItemType Directory -Force -Path $models | Out-Null

$model = Join-Path $models "model.onnx"
$vocab = Join-Path $models "vocab.txt"

if (-not (Test-Path $model)) {
    Write-Host "Downloading model.onnx (~90MB)…"
    Invoke-WebRequest -UseBasicParsing `
        -Uri "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx" `
        -OutFile $model
}
Write-Host ("model.onnx: {0:N0} bytes" -f (Get-Item $model).Length)

if (-not (Test-Path $vocab)) {
    Write-Host "Downloading vocab.txt…"
    Invoke-WebRequest -UseBasicParsing `
        -Uri "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt" `
        -OutFile $vocab
}
Write-Host ("vocab.txt: {0:N0} bytes" -f (Get-Item $vocab).Length)

Write-Host "Done. Model ready at src\models\."
