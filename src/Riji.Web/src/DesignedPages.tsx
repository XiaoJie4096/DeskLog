import { useState } from 'react';
import type { Snapshot } from './bridge';
import { categoryTotals } from './ui-data';
import { activityTime } from './day-time';

export const formatTime = (seconds: number) => {
  const total = Math.max(0, Math.floor(seconds));
  if (total < 60) return `${total} 秒`;
  const hours = Math.floor(total / 3600), minutes = Math.floor(total % 3600 / 60);
  return hours ? `${hours} 时${minutes ? ` ${minutes} 分` : ''}` : `${minutes} 分`;
};

export function TodayOverview({ state, review }: { state: Snapshot; review: () => void }) {
  const records = [...state.recognition.records].sort((a, b) => a.utc.localeCompare(b.utc));
  const total = records.reduce((sum, r) => sum + r.seconds, 0);
  const groups = categoryTotals(records);
  const clock = (utc: string) => activityTime(utc, state.day, state.settings);
  let offset = 0;
  const slices = groups.map(g => { const start = offset; offset += g.seconds / total * 100; return `${g.color} ${start}% ${offset}%`; });
  return <div className="home-grid original-home">
    <section className="panel duration-panel"><p>今天成功识别累计</p><div className="big-time">{Math.floor(total / 3600)}<small>小时</small>{Math.floor(total % 3600 / 60)}<small>分钟</small></div><div className="micro"><div><small>首次记录</small><strong>{records.length ? clock(records[0].utc) : '—'}</strong></div><div><small>最近记录</small><strong>{records.length ? clock(records[records.length - 1].utc) : '—'}</strong></div><div><small>成功识别</small><strong>{records.length} 次</strong></div></div><small className="sampling-caption">按成功采样间隔累计，不等同于前台应用时长。</small></section>
    <section className="panel distribution-panel"><div className="section-head"><h2>时间，花在哪里</h2><span>↗</span></div>{total > 0 ? <div className="distribution"><div className="pie" role="img" aria-label={groups.map(g => `${g.name} ${(g.seconds / total * 100).toFixed(1)}%`).join('，')} style={{ background: `conic-gradient(${slices.join(',')})` }} /><div className="legend">{groups.map((g, i) => <div key={i}><i style={{ background: g.color }} /><span>{g.name}</span><b>{formatTime(g.seconds)} · {(g.seconds / total * 100).toFixed(1)}%</b></div>)}</div></div> : <p className="empty">暂无成功识别记录</p>}<div className="ornament">❋ <small>SMALL MOMENTS, YOUR DAY.</small></div></section>
    <section className="panel recent-panel"><div className="section-head"><h2>刚刚留下的片段</h2><button onClick={review}>回顾今天 ↗</button></div>{records.slice(-3).reverse().map(r => <div className="recent-record" key={r.id}><time>{clock(r.utc)}</time><span className="category-label" style={{ borderColor: r.category.color }}>{r.category.name}</span><p>{r.description}</p></div>)}{!records.length && <p>今天还没有活动记录。</p>}</section>
  </div>;
}

export function ApplicationStatistics({ state }: { state: Snapshot }) {
  const [selection, setSelection] = useState('');
  const items = [...state.apps].sort((a, b) => b.seconds - a.seconds).map(a => ({ ...a, key: JSON.stringify([a.appId, a.name]) }));
  const total = items.reduce((sum, item) => sum + item.seconds, 0);
  const selected = items.find(item => item.key === selection) ?? items[0];
  const details = selected ? state.websites.filter(s => s.appId === selected.appId && s.appName === selected.name) : [];
  return <>
    <div className="stat-total"><div><small>应用使用总时长</small><strong>{formatTime(total)}</strong></div><p>每段前台时间只计入一个应用。<br />匹配规则的网站独立显示，其他时间计入浏览器。</p></div>
    <div className="stat-toolbar"><small>按使用时长排序 · 规则修改只影响之后的记录</small></div>
    {state.browserError && <p role="alert">{state.browserError}</p>}
    <div className="stat-grid"><section className="panel"><div className="section-head"><h2>应用使用分布</h2><small>{items.length} 项</small></div>
      <div className="app-list">{items.map(item => <button className="app-row" key={item.key} aria-pressed={item.key === selected?.key} onClick={() => setSelection(item.key)}>
        <span className="badge">{item.name.slice(0, 2)}</span><span className="app-info"><strong>{item.name}</strong><span className="track"><i style={{ width: `${items[0]?.seconds ? item.seconds / items[0].seconds * 100 : 0}%` }} /></span></span>
        <span className="app-time">{formatTime(item.seconds)}<small>{total ? (item.seconds / total * 100).toFixed(1) : 0}%</small></span>
      </button>)}</div>{!items.length && <p className="empty">这一天还没有应用记录。</p>}
    </section><section className="panel stats-detail">{selected ? <>
      <span className="badge">{selected.name.slice(0, 2)}</span><h2>{selected.name}</h2><p>前台有效使用</p><div className="detail-number">{formatTime(selected.seconds)}</div><small>{state.day}</small>
      <p>占应用总时长 {total ? (selected.seconds / total * 100).toFixed(1) : 0}%</p>
      {!!details.length && <div className="site-pages"><h3>网站记录与来源</h3>{details.map((site, index) => <div key={index}><p>{site.title || site.domain}</p><small>{site.domain} · {site.sourceAppName} · {formatTime(site.seconds)}</small></div>)}<p>以上是该应用的记录明细，不额外计入总时长。网页标题仅在开启后保存。</p></div>}
    </> : <p>选择一项查看详情。</p>}</section></div>
  </>;
}
