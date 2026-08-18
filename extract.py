import json, sys, os

path = r'C:\Users\Qin_zzq\AppData\Local\Temp\sketch_content\pages\8F01190B-C2E6-4327-86D7-0175AB21E41F.json'
print(f"File exists: {os.path.exists(path)}")
print(f"File size: {os.path.getsize(path)}")

data = json.load(open(path, 'rb'))

def find_key(obj, target):
    results = []
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k == target:
                results.append(v)
            results.extend(find_key(v, target))
    elif isinstance(obj, list):
        for item in obj:
            results.extend(find_key(item, target))
    return results

# Get all colors from fills
fills = find_key(data, 'fills')
for f in fills[:30]:
    if isinstance(f, list) and len(f) > 0:
        c = f[0].get('color', {})
        if c:
            r, g, b = c.get('red', 0), c.get('green', 0), c.get('blue', 0)
            hex_color = '#{:02x}{:02x}{:02x}'.format(int(r*255),int(g*255),int(b*255))
            print(f'Color: {hex_color}')

# Get font sizes
texts = find_key(data, 'fontSize')
for t in texts[:20]:
    print(f'FontSize: {t}')

# Get artboard sizes
frames = find_key(data, 'frame')
for f in frames:
    if isinstance(f, dict) and 'width' in f and 'height' in f:
        print(f'Frame: {f.get("width")}x{f.get("height")}')
