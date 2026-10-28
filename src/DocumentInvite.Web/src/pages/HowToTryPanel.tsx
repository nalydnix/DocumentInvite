import { useState } from 'react';
import { Link } from 'react-router-dom';
import { secondaryButtonClass } from '../components/Field';
import { useDemoConfig } from './demo';

const DISMISSED_KEY = 'di.howToTry.dismissed';

function readDismissed(): boolean {
  try {
    return localStorage.getItem(DISMISSED_KEY) === '1';
  } catch {
    return false;
  }
}

function writeDismissed(): void {
  try {
    localStorage.setItem(DISMISSED_KEY, '1');
  } catch {
    // Storage can be unavailable; the panel then comes back on the next visit.
  }
}

/** A short walkthrough for the demo. Shown only in demo mode, until the visitor dismisses it. */
export function HowToTryPanel() {
  const demo = useDemoConfig();
  const [dismissed, setDismissed] = useState(readDismissed);

  if (!demo.enabled || dismissed) return null;

  function dismiss() {
    writeDismissed();
    setDismissed(true);
  }

  return (
    <section
      aria-label="How to try this"
      className="rounded-card border border-brand-200 bg-brand-50 p-4 shadow-card"
    >
      <div className="flex items-start justify-between gap-3">
        <h2 className="text-base font-semibold text-ink">How to try this</h2>
        <button type="button" onClick={dismiss} className={secondaryButtonClass} aria-label="Dismiss how to try this">
          Dismiss
        </button>
      </div>
      <ol className="mt-2 list-decimal space-y-1 pl-5 text-sm text-ink">
        <li>Sign in with a demo login. The Admin login can do everything.</li>
        <li>
          Open the <Link className="font-medium text-brand-700 underline" to="/outbox">Outbox</Link> to see the emails the
          system sent to the borrowers.
        </li>
        <li>Open a portal link from an email as the borrower, then upload a file or fill in a field.</li>
        <li>
          Review as the lender: open a <Link className="font-medium text-brand-700 underline" to="/loans">loan</Link>,
          preview the files, then approve or reject each item.
        </li>
        <li>
          Check the <Link className="font-medium text-brand-700 underline" to="/audit">audit log</Link> to see every step
          recorded.
        </li>
      </ol>
    </section>
  );
}
