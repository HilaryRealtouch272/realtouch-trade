import json,tempfile,unittest
from pathlib import Path
from execution_reporter import Reporter
class ReportingTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.path=Path(self.tmp.name)/'r.db';self.sent=[]
  self.row=dict(id='main:one:0',ticket=123,entry_price=100,opened_at=101,status='Open',payload=json.dumps(dict(side='Long',flow='main')),symbol='BTCUSDm',volume=.01,stop=90,target=110,pnl=5,reason='Confirmed broker exit')
 def tearDown(self):self.tmp.cleanup()
 def sender(self,text):
  self.sent.append(text);return dict(ok=True,result=dict(message_id=len(self.sent),chat=dict(id=-5446001871)))
 def test_entry_before_close_and_restart_dedup(self):
  r=Reporter(self.path,-5446001871,self.sender,100);r.report([self.row]);r.db.close()
  r=Reporter(self.path,-5446001871,self.sender,200);self.row['status']='Closed';r.report([self.row]);r.report([self.row]);r.db.close()
  self.assertEqual(2,len(self.sent));self.assertIn('EXECUTION',self.sent[0]);self.assertIn('EXIT',self.sent[1])
 def test_timeout_never_retries_or_sends_exit(self):
  def lost(text):self.sent.append(text);raise TimeoutError()
  r=Reporter(self.path,-5446001871,lost,100);r.report([self.row]);self.row['status']='Closed';r.report([self.row]);r.db.close();self.assertEqual(1,len(self.sent))
 def test_account_label_keeps_existing_delivery_identity(self):
  r=Reporter(self.path,-5446001871,self.sender,100,account_label='MAIN demo 7331');r.report([self.row]);r.db.close()
  r=Reporter(self.path,-5446001871,self.sender,100,account_label='MAIN demo 7331');r.report([self.row]);r.db.close()
  self.assertEqual(1,len(self.sent));self.assertIn('Account: MAIN demo 7331',self.sent[0])
 def test_old_closed_history_not_broadcast(self):
  self.row['status']='Closed';r=Reporter(self.path,-5446001871,self.sender,200);r.report([self.row]);r.db.close();self.assertEqual([],self.sent)
 def test_live_main_tp2_label_is_not_demo_or_tp1(self):
  self.row['target']=120
  r=Reporter(self.path,-5446001871,self.sender,100,execution_mode='live',main_exit_mode='tp2');r.report([self.row]);r.db.close()
  self.assertIn('LIVE BOT EXECUTION',self.sent[0]);self.assertIn('TP2: 120',self.sent[0]);self.assertNotIn('DEMO',self.sent[0])
 def test_rejected_entry_notice_is_explicit_and_durable(self):
  row=dict(id='main:skip',status='Rejected',reason='Broker entry has drifted too far',payload=json.dumps(dict(flow='main',symbol='GBP/USD',side='Long',entry=1.30938,qualifiedAtUtc='2026-11-27T01:31:30Z')))
  r=Reporter(self.path,-5446001871,self.sender,100,execution_mode='live');r.report_rejections([row]);r.db.close()
  r=Reporter(self.path,-5446001871,self.sender,100,execution_mode='live');r.report_rejections([row]);r.db.close()
  self.assertEqual(1,len(self.sent));self.assertIn('ENTRY SKIPPED',self.sent[0]);self.assertIn('No broker fill',self.sent[0]);self.assertNotIn('BOT EXECUTION',self.sent[0])
 def test_old_rejections_are_not_replayed(self):
  row=dict(id='main:old',status='Rejected',reason='expired',payload=json.dumps(dict(qualifiedAtUtc='1970-01-01T00:00:01Z')))
  r=Reporter(self.path,-5446001871,self.sender,100);r.report_rejections([row]);r.db.close();self.assertEqual([],self.sent)
 def test_waiting_for_zone_notice_does_not_claim_execution_or_final_rejection(self):
  row=dict(id='main:wait',status='PendingEntry',reason='Broker entry is outside the qualified zone',payload=json.dumps(dict(flow='main',symbol='GBP/USD',side='Long',entry=1.30938,qualifiedAtUtc='2026-11-27T01:31:30Z')))
  r=Reporter(self.path,-5446001871,self.sender,100);r.report_rejections([row]);r.report_rejections([row]);r.db.close()
  self.assertEqual(1,len(self.sent));self.assertIn('WAITING FOR ENTRY',self.sent[0]);self.assertIn('Will recheck',self.sent[0]);self.assertNotIn('ENTRY SKIPPED',self.sent[0])
if __name__=='__main__':unittest.main()
