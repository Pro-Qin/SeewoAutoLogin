import json, sys

data = json.load(open('/tmp/sketch_content/pages/8F01190B-C2E6-4327-86D7-0175AB21E41F.json'))

def walk(obj, depth=0):
    if depth > 10: return
    if isinstance(obj, dict):
        name = obj.get('name','')
        cls = obj.get('_class','')
        frame = obj.get('frame', {})
        if cls and name:
            r = frame if isinstance(frame, dict) else {}
            print(f'{"  "*depth}{cls}: {name} ({r.get("width","?")}x{r.get("height","?")})')
        for v in obj.values():
            walk(v, depth+1)
    elif isinstance(obj, list):
        for item in obj:
            walk(item, depth)

walk(data)
