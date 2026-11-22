import json,sys
d=json.load(open(sys.argv[1],encoding="utf-8")); k=lambda o,n:o[{x.lower():x for x in o}[n]]
n=k(d,"notes"); h=k(n,"highlights"); c=k(n,"comments")
print(len(h),"Markierungen,",len(c),"Kommentare")
for x in h[:int(sys.argv[2]) if len(sys.argv)>2 else 0]: print("    ",k(x,"color"),"|",k(x,"text")[:80])
for x in c[:int(sys.argv[3]) if len(sys.argv)>3 else 0]: print("    CM",k(x,"text")[:50],"| Anker:",k(x,"anchor")[:40])
