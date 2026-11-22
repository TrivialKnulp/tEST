import time,sys
from aiosmtpd.controller import Controller
from email import message_from_bytes
from email.header import decode_header,make_header
class H:
    async def handle_DATA(self,server,session,envelope):
        r=message_from_bytes(envelope.content); open("/tmp/smtp_got.txt","a").write(str(make_header(decode_header(r["Subject"])))+" | "+str(envelope.rcpt_tos)+"\n"); return "250 OK"
c=Controller(H(),hostname="127.0.0.1",port=2526); c.start(); time.sleep(3600)
