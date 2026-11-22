import json,urllib.request,time,sys,os
B="http://127.0.0.1:5077"
def post(path,body,t=600):
    r=urllib.request.Request(B+path,json.dumps(body).encode(),{"Content-Type":"application/json"})
    try: return json.load(urllib.request.urlopen(r,timeout=t))
    except urllib.error.HTTPError as e: return json.load(e)
scan=post("/scan",{"dir":"/home/claude/q","recursive":True})
res=[]
for it in scan["items"]:
    t0=time.time()
    a=post("/analyze",{"pdf":it["pdf"],"docx":it["docx"],"other":it["doc"]})
    row={"name":it["baseName"][:48],"typ":"pdf" if it["pdf"] else "docx" if it["docx"] else "doc"}
    if not a.get("ok"): row["fehler"]=str(a.get("error"))[:90]; res.append(row); continue
    row.update(seiten=a["pages"],hl=len(a["notes"]["highlights"]))
    g=post("/generate",{"irId":a["irId"],"name":os.path.splitext(os.path.basename(it["pdf"] or it["docx"] or it["doc"]))[0][:40],"outDir":"/tmp/api/out/test","preview":True,"useQdrant":False})
    if not g.get("ok"): row["fehler"]="generate: "+str(g.get("error"))[:90]
    else: row.update(art=g.get("kind") or g.get("art"), stf=bool(g.get("stf")), ausdruck=bool(g.get("filledPdf")), unbekannt=len(g.get("filledUnknown",[])))
    row["s"]=round(time.time()-t0,1); res.append(row)
ok=sum(1 for r in res if "fehler" not in r)
print(f"{ok}/{len(res)} ohne Fehler")
for r in res: print(r)
