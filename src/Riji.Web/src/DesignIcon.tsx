// Shared line icons for application navigation.
export function DesignIcon({ name }: { name: string }) {
  const paths: Record<string, React.ReactNode> = {
    sun: <><circle cx="12" cy="12" r="4" /><path d="M12 1v3m0 16v3M1 12h3m16 0h3M4 4l2 2m12 12 2 2M4 20l2-2M18 6l2-2" /></>,
    home: <path d="M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z" />,
    review: <path d="M5 4h14v17H5zM8 2v4m8-4v4M5 9h14m-10 4h6m-6 4h4" />,
    settings: <><path d="M4 6h16M4 12h16M4 18h16" /><circle cx="8" cy="6" r="2" /><circle cx="16" cy="12" r="2" /><circle cx="10" cy="18" r="2" /></>,
    statistics: <path d="M4 20V10h4v10m4 0V4h4v16m4 0v-7h3v7M2 20h22" />,
    summaries: <path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4z" />
  };
  return <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name] ?? paths.sun}</svg>;
}
