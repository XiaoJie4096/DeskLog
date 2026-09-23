import type { Settings } from './bridge';

export function NightSettings({ settings, disabled, configure }: { settings: Settings | undefined; disabled: boolean; configure: (patch: Partial<Settings>) => void }) {
  return <section className="panel settings night-settings"><h2>一天的起点</h2>
    <label className="setting"><span>夜猫子模式<small>今天从设定时间开始；历史回顾、统计和按天总结会按当前设置重新归类。</small></span><input type="checkbox" disabled={disabled} checked={settings?.nightMode ?? false} onChange={event => configure({ nightMode: event.target.checked })} /></label>
    <label className="setting"><span>新一天开始时间</span><select disabled={disabled || !settings?.nightMode} value={settings?.dayStartHour ?? 5} onChange={event => configure({ dayStartHour: Number(event.target.value) })}>{Array.from({ length: 9 }, (_, i) => i + 1).map(hour => <option key={hour} value={hour}>{String(hour).padStart(2, '0')}:00</option>)}</select></label>
    <label className="setting"><span>延长小时显示<small>开启时次日 01:00 显示为 25:00；关闭时显示为“次日 01:00”。</small></span><input type="checkbox" disabled={disabled || !settings?.nightMode} checked={settings?.extendedHours ?? false} onChange={event => configure({ extendedHours: event.target.checked })} /></label>
  </section>;
}
