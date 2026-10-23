import {describe,it,expect} from 'vitest';
import {metricIsCurrent} from './Usage';
import type {ProviderUsageMetric} from '../lib/models';
describe('provider measurement freshness',()=>{
  const now=Date.parse('2026-10-23T15:00:00Z');
  const measured:ProviderUsageMetric={used:0,limit:100,periodStart:'2026-10-23T00:00:00Z',periodEnd:'2026-10-24T00:00:00Z',observedAt:'2026-10-23T14:00:00Z',source:'provider dashboard'};
  it('distinguishes a measured zero from missing measurements',()=>{expect(metricIsCurrent(measured,now)).toBe(true);expect(metricIsCurrent({...measured,used:null},now)).toBe(false);});
  it('rejects stale, future and out-of-period measurements',()=>{
    expect(metricIsCurrent({...measured,observedAt:'2026-10-22T13:00:00Z'},now)).toBe(false);
    expect(metricIsCurrent({...measured,observedAt:'2026-10-23T15:10:00Z'},now)).toBe(false);
    expect(metricIsCurrent({...measured,periodEnd:'2026-10-23T15:00:00Z'},now)).toBe(false);
  });
});
