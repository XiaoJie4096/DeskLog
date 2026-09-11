import { useEffect, useRef, useState } from 'react';

export function ModeHelp() {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { setOpen(false); trigger.current?.focus(); }
    };
    document.addEventListener('pointerdown', outside);
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('pointerdown', outside);
      document.removeEventListener('keydown', escape);
    };
  }, [open]);
  return <div className="mode-help-anchor" ref={root}>
    <button ref={trigger} className="mode-help" type="button" aria-label="记录状态说明" aria-expanded={open} aria-controls="mode-help-content" onClick={() => setOpen(value => !value)}>ⓘ</button>
    {open && <div id="mode-help-content" className="mode-help-popover">
      <strong>记录状态说明</strong>
      <span><b>默认</b> 按设置记录，空闲时自动进入离开。</span>
      <span><b>离开</b> 暂停记录，30 秒冷却后检测到操作可恢复。</span>
      <span><b>锁定</b> 持续记录，不自动切到离开状态，适合看视频等不操作的场景。</span>
      <span><b>不识屏</b> 停止截图和识别，继续应用计时。</span>
    </div>}
  </div>;
}
