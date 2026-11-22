import json,re,threading
from http.server import BaseHTTPRequestHandler,HTTPServer
cols={"Formularerstellung":{}}
class H(BaseHTTPRequestHandler):
    def log_message(s,*a): pass
    def _j(s,code,obj): b=json.dumps(obj).encode(); s.send_response(code); s.send_header("Content-Type","application/json"); s.send_header("Content-Length",str(len(b))); s.end_headers(); s.wfile.write(b)
    def _body(s): n=int(s.headers.get("Content-Length",0)); return json.loads(s.rfile.read(n) or b"{}")
    def do_GET(s):
        if s.path=="/api/tags": return s._j(200,{"models":[{"name":"bge-m3:latest"},{"name":"qwen3.8:27b"}]})
        m=re.match(r"^/collections/([^/]+)$",s.path)
        if m: 
            c=m.group(1)
            return s._j(200,{"result":{"status":"green","points_count":len(cols[c])}}) if c in cols else s._j(404,{})
        s._j(404,{})
    def do_PUT(s):
        b=s._body(); m=re.match(r"^/collections/([^/]+)(/points.*)?$",s.path)
        if not m: return s._j(404,{})
        c,rest=m.group(1),m.group(2)
        if rest is None: cols[c]={}; return s._j(200,{"result":True})
        if c not in cols: return s._j(404,{})
        for p in b["points"]: cols[c][str(p["id"])]=p.get("payload",{})
        s._j(200,{"result":{"status":"completed"}})
    def do_DELETE(s):
        m=re.match(r"^/collections/([^/]+)$",s.path)
        if m: cols.pop(m.group(1),None); return s._j(200,{"result":True})
        s._j(404,{})
    def do_POST(s):
        b=s._body()
        m=re.match(r"^/collections/([^/]+)/points/scroll$",s.path)
        if m:
            c=m.group(1)
            if c not in cols: return s._j(404,{})
            ids=sorted(cols[c]); off=int(b.get("offset") or 0); lim=b.get("limit",10); page=ids[off:off+lim]; nx=off+lim if off+lim<len(ids) else None
            return s._j(200,{"result":{"points":[{"id":i,"payload":cols[c][i]} for i in page],"next_page_offset":nx}})
        if s.path=="/api/embeddings": return s._j(200,{"embedding":[0.1]*8})
        if s.path=="/api/chat": return s._j(200,{"message":{"role":"assistant","content":"{\"ok\": true, \"feld\": \"Datum\"}"}})
        s._j(404,{})
HTTPServer(("127.0.0.1",6399),H).serve_forever()
