import { Fragment } from 'react';

// Render a small formatting subset with React text nodes; provider HTML is never interpreted.
export function SummaryText({ text }: { text: string }) {
  return <div className="summary-text">{text.split('\n').map((line, index) => {
    const heading = /^(#{1,6})\s+(.+)$/.exec(line);
    const bullet = /^\s*[-*]\s+(.+)$/.exec(line);
    const content = heading?.[2] ?? bullet?.[1] ?? line;
    const formatted = content.split(/(\*\*[^*\n]+\*\*)/g).map((part, partIndex) =>
      part.startsWith('**') && part.endsWith('**') ? <strong key={partIndex}>{part.slice(2, -2)}</strong> : <Fragment key={partIndex}>{part}</Fragment>);
    return heading ? <h3 key={index}>{formatted}</h3> : <div key={index} className={bullet ? 'summary-bullet' : undefined}>{bullet && <span aria-hidden="true">• </span>}{formatted}{!line && <br />}</div>;
  })}</div>;
}
