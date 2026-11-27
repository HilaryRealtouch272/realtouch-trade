"""Read-only execution reporter. Never connects to MT5 or places orders."""
import argparse, json, sqlite3, ssl, time, urllib.request
from pathlib import Path

class Reporter:
    def __init__(self, state, chat, send, now=None, account_label=None, execution_mode="demo", main_exit_mode=None):
        self.db=sqlite3.connect(state,isolation_level=None)
        self.db.execute('pragma synchronous=FULL')
        self.db.execute('create table if not exists notices(id text primary key,status text,message_id integer)')
        self.db.execute('create table if not exists settings(id text primary key,value integer)')
        self.db.execute("insert or ignore into settings values('cutoff',?)",(int(time.time() if now is None else now),))
        self.cutoff=self.db.execute("select value from settings where id='cutoff'").fetchone()[0]
        self.chat=str(chat);self.send=send;self.account_label=account_label
        if execution_mode not in ("demo", "live"): raise ValueError("Unknown execution mode")
        self.mode_label=execution_mode.upper();self.main_exit_mode=main_exit_mode

    def notice(self, key, text):
        key=self.chat+':'+key
        row=self.db.execute('select status from notices where id=?',(key,)).fetchone()
        if row: return row[0]=='Delivered'
        # Persist before network I/O. An ambiguous request is never blindly resent.
        self.db.execute("insert into notices values(?,'Uncertain',null)",(key,))
        try:
            result=self.send(("🏦 Account: "+self.account_label+"\n" if self.account_label else "")+text)
            if result.get('ok') is not True: return False
            message=result['result']
            if str(message['chat']['id'])!=self.chat: return False
            self.db.execute("update notices set status='Delivered',message_id=? where id=?",(message['message_id'],key))
            print(json.dumps({'delivered':True,'event':key,'messageId':message['message_id'],'chatId':self.chat}),flush=True)
            return True
        except Exception:
            print('Telegram delivery uncertain; automatic resend withheld.',flush=True)
            return False

    def report(self, rows):
        for r in rows:
            if r['ticket'] is None or r['entry_price'] is None: continue
            entry_key=r['id']+':entry'
            prior=self.db.execute('select status from notices where id=?',(self.chat+':'+entry_key,)).fetchone()
            if r['status']=='Closed' and not prior and (r['opened_at'] or 0)<self.cutoff: continue
            s=json.loads(r['payload']); side=('🟢 ' if s['side']=='Long' else '🔴 ')+s['side'].upper()
            old=(r['opened_at'] or 0)<self.cutoff
            title=f'🔄 CURRENT {self.mode_label} POSITION' if old else f'🤖 {self.mode_label} BOT EXECUTION'
            target_label='VTB target' if s['flow']=='vtb' else ('TP2' if self.main_exit_mode=='tp2' else 'TP'+str(int(r['id'].rsplit(':',1)[1])+1))
            entry=(f"{title} | {s['flow'].upper()} | {r['symbol']} | {side}\n"
                   f"📦 Volume: {r['volume']:.2f} lot\n📍 Filled entry: {r['entry_price']}\n"
                   f"🛑 Stop-loss: {r['stop']}\n🎯 {target_label}: {r['target']}\n🎫 Ticket: {r['ticket']}")
            if old: entry+='\nAlready open; tracking continues on AWS.'
            if not self.notice(entry_key,entry): continue
            if r['status']=='Closed':
                remaining=sum(x['volume'] or 0 for x in rows if x['id'].rsplit(':',1)[0]==r['id'].rsplit(':',1)[0] and x['status']=='Open')
                label=f'💰 {self.mode_label} BOT PARTIAL EXIT' if remaining else f'🏁 {self.mode_label} BOT EXIT'
                reason='Closure confirmed by broker.'
                for code,description in [('4','🛑 Stop-loss filled.'),('5','🎯 Take-profit filled.'),('6','⚠️ Broker stop-out.'),('3','Bot exit confirmed.')]:
                    if r['reason'].endswith('exit reason '+code): reason=description
                text=(f"{label} | {s['flow'].upper()} | {r['symbol']} | {side}\n"
                      f"Closed volume: {r['volume']:.2f} lot | {target_label}\n"
                      f"💵 Realized P/L: {r['pnl']:+.2f} USD\n🎫 Ticket: {r['ticket']}\n{reason}\nRemaining tracked volume: {remaining:.2f} lot")
                self.notice(r['id']+':closed',text)
            elif r['status']=='Uncertain':
                self.notice(r['id']+':review',f"⚠️ {self.mode_label} BOT TRACKING REVIEW | {r['symbol']}\nTicket: {r['ticket']}\n{r['reason']}\nBroker closure has not been confirmed.")

    def report_rejections(self, rows):
        from datetime import datetime
        for row in rows:
            if row['status'] != 'Rejected': continue
            s=json.loads(row['payload'])
            qualified=datetime.fromisoformat(s['qualifiedAtUtc'].replace('Z','+00:00')).timestamp()
            if qualified < self.cutoff: continue  # Never broadcast old missed history on installation.
            message=(f"⚠️ {self.mode_label} BOT ENTRY SKIPPED | {s['flow'].upper()} | {s['symbol']}\n"
                     f"Direction: {s['side'].upper()} | Signal entry: {s['entry']}\n"
                     f"Reason: {row['reason'][:800]}\nNo broker fill was confirmed for this signal.")
            self.notice(row['id']+':rejected',message)

def main():
    p=argparse.ArgumentParser();p.add_argument('--config',required=True);p.add_argument('--once',action='store_true');a=p.parse_args()
    c=json.loads(Path(a.config).read_text(encoding='utf-8-sig'))
    assert str(c['chatId'])=='-5446001871','Unexpected beta destination'
    context=ssl.create_default_context(cafile=c['caBundle'])
    def send(text):
        body=json.dumps({'chat_id':c['chatId'],'text':text}).encode()
        req=urllib.request.Request('https://api.telegram.org/bot'+c['botToken']+'/sendMessage',data=body,headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(req,timeout=20,context=context) as response: return json.load(response)
    import msvcrt
    with open(c['receiptDatabase']+'.lock','a+b') as lock:
        lock.seek(0);msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
        reporter=Reporter(c['receiptDatabase'],c['chatId'],send,account_label=c.get('accountLabel'),execution_mode=c.get('executionMode','demo'),main_exit_mode=c.get('mainExitMode'))
        while True:
            try:
                db=sqlite3.connect('file:'+Path(c['executionDatabase']).as_posix()+'?mode=ro',uri=True);db.row_factory=sqlite3.Row
                rows=db.execute('select legs.*,signals.payload from legs join signals on signals.id=legs.signal_id where legs.status in (\'Open\',\'Closed\',\'Uncertain\') order by legs.opened_at,legs.id').fetchall()
                rejected=db.execute("select id,payload,status,reason from signals where status='Rejected'").fetchall();db.close()
                reporter.report(rows)
                reporter.report_rejections(rejected)
            except Exception: print('Reporter check failed; execution worker is unaffected.',flush=True)
            if a.once: break
            time.sleep(5)
if __name__=='__main__':main()
