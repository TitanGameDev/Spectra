import type { ReactNode } from "react";

interface PlaceholderPageProps {
  title: string;
  tagline: string;
  icon: ReactNode;
}

export default function PlaceholderPage({ title, tagline, icon }: PlaceholderPageProps) {
  return (
    <>
      <div className="dashboard-intro">
        <h1>{title}</h1>
        <p>{tagline}</p>
      </div>

      <div className="panel-empty">
        <svg className="panel-empty-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true">
          {icon}
        </svg>
        <h2>Coming soon</h2>
        <p>{title} isn't wired up yet — this is where it'll live.</p>
      </div>
    </>
  );
}
