"""开发回归：人工选择的可见缺墨点必须落在候选框内。
这里只验证候选框覆盖，不代表像素级真值或工业召回率。
用法：python check_barcode_point.py <BarcodeRegression的UTF-8日志> <原图X坐标> <原图Y坐标>
"""
import re
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8-sig")
x, y = map(int, sys.argv[2:4])
boxes = re.findall(r"Ng (?:barcode_missing_ink|barcode_ink_loss) box=\[(\d+),(\d+),(\d+),(\d+)\]", text)
covered = any(a <= x < a + w and b <= y < b + h for a, b, w, h in (map(int, box) for box in boxes))
print(f"{'PASS' if covered else 'FAIL'}: selected original-pixel ink-loss point ({x},{y}) candidate-box coverage={covered}")
sys.exit(0 if covered else 1)
