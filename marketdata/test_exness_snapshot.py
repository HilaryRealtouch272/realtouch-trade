import unittest
from datetime import datetime,timezone,timedelta
from exness_snapshot import daily_from_hourly
class Aggregation(unittest.TestCase):
 def rows(self):
  start=datetime(2025,1,1,tzinfo=timezone.utc)
  return [dict(time=int((start+timedelta(days=d,hours=h)).timestamp()),open=100,high=103,low=98,close=101)
   for d in range(360) if (start+timedelta(days=d)).weekday()<5 for h in range(10)]
 def test_utc_ohlc_and_no_weekend_bars(self):
  bars=daily_from_hourly(self.rows(),datetime(2026,1,1,tzinfo=timezone.utc))
  self.assertGreater(len(bars),220)
  self.assertEqual(bars[0]['open'],100)
  self.assertEqual(bars[0]['high'],103)
  self.assertEqual(bars[0]['low'],98)
  self.assertTrue(all(datetime.fromisoformat(b['openTimeUtc'].replace('Z','+00:00')).weekday()<5 for b in bars))
 def test_duplicates_and_bad_prices_rejected(self):
  rows=self.rows()
  with self.assertRaises(ValueError):daily_from_hourly(rows+[rows[0]],datetime(2026,1,1,tzinfo=timezone.utc))
  rows[0]['close']=999
  with self.assertRaises(ValueError):daily_from_hourly(rows,datetime(2026,1,1,tzinfo=timezone.utc))
 def test_short_history_rejected(self):
  with self.assertRaises(ValueError):daily_from_hourly(self.rows()[:20],datetime(2026,1,1,tzinfo=timezone.utc))
if __name__=='__main__':unittest.main()
