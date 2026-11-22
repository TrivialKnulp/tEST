import json,urllib.request
B="http://127.0.0.1:5077"
def post(path,body):
    r=urllib.request.Request(B+path,json.dumps(body).encode(),{"Content-Type":"application/json"})
    try: return json.load(urllib.request.urlopen(r,timeout=300))
    except urllib.error.HTTPError as e: return json.load(e)
PDF="/tmp/pdfonly/wg.pdf"; W="/home/claude/q/Neuer Bescheid/2025 05 13 Muster Wohnungsgeberbest#U00e4tigung Muster.docx"
def gen(label,docx,out):
    a=post("/analyze",{"pdf":PDF,"docx":docx,"other":None})
    g=post("/generate",{"irId":a["irId"],"name":out,"outDir":"/tmp/api/out/t11","useQdrant":False})
    print(f"\n== {label}: ok={g.get('ok')} Markierungen ersetzt={g.get('textMapped')}")
    for x in g.get("autoPlaceholders") or []: print("   +",x[:105])
    for x in g.get("unassignedHighlights") or []: print("   ?",x[:120])
    return g
print("Katalog:",len(post("/placeholders",{"dir":"/tmp/corpus","top":100})["entries"]),"Platzhalter im Korpus")
gen("A) PDF ohne Gedächtnis (nur Katalog)",None,"A")
gen("B) PDF + Word mit Kommentaren (lernt)",W,"B")
gen("C) PDF ohne Word, jetzt mit Gedächtnis",None,"C")
