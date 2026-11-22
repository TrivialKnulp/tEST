#!/bin/bash
# Testbuild (net8, ohne System.Drawing) aus /home/claude/v11/StfKit nach /tmp/build11
set -e
rm -rf /tmp/build11 && mkdir -p /tmp/build11 && cp -r /home/claude/v11/StfKit/src /home/claude/v11/StfKit/tools /tmp/build11/ 
cd /tmp/build11
find . -name "*.csproj" -exec sed -i 's#net5.0#net8.0#' {} \;
sed -i '/System.Drawing.Common/d' src/StfKit.Pdf/StfKit.Pdf.csproj
python3 - <<'P'
import re
p="src/StfKit.Pdf/PdfExtractor.cs"; s=open(p,encoding="utf-8").read()
s=s.replace("using var bmp = System.Drawing.Image.FromStream(ms);","throw new NotSupportedException();")
s=re.sub(r"bmp\.Save\(o, System\.Drawing\.Imaging\.ImageFormat\.Png\);","",s)
open(p,"w",encoding="utf-8").write(s)
P
for pr in src/StfKit.Pdf src/StfKit.Cli src/StfKit.Api tools/StfKit.Examples; do dotnet restore $pr --source /tmp/feed --source /tmp/emptysrc -nologo -v q 2>&1 | grep -E "error" | head -3; done
dotnet build src/StfKit.Cli -c Release --no-restore -nologo -v q 2>&1 | grep -E "error|Build succeeded|Warn.*0" | sort -u | head
dotnet build src/StfKit.Api -c Release --no-restore -nologo -v q 2>&1 | grep -E "error|Build succeeded" | sort -u | head
dotnet build tools/StfKit.Examples -c Release --no-restore -nologo -v q 2>&1 | grep -E "error|Build succeeded" | sort -u | head
