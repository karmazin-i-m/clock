import { useEffect, useRef, type ReactNode } from 'react';
import { useI18n } from '../i18n';

// State is never colour alone: always a dot and a word (HANDOFF §3).
export function StatusBadge({ status }: { status: 'online' | 'offline' | 'readonly' }) {
  const { t } = useI18n();
  const label = status === 'online' ? t.common.online : status === 'offline' ? t.common.offline : t.common.readonly;
  return (
    <span className={`badge badge-${status}`}>
      <span className="dot" aria-hidden="true" />
      {label}
    </span>
  );
}

export function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: readonly { id: T; label: string }[];
  onChange: (value: T) => void;
}) {
  return (
    <div role="group" aria-label={label} className="segmented">
      {options.map((o) => (
        <button key={o.id} type="button" aria-pressed={o.id === value} onClick={() => onChange(o.id)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

/**
 * A modal confirmation on the native <dialog>: focus is trapped, Escape closes it, and the
 * page behind is inert — none of which needs to be written again by hand.
 */
export function ConfirmDialog({
  open,
  title,
  children,
  confirm,
  busy,
  onConfirm,
  onClose,
}: {
  open: boolean;
  title: string;
  children: ReactNode;
  confirm: string;
  busy?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog ref={ref} className="dialog" aria-labelledby="dialog-title" onClose={onClose}>
      <div className="dialog-body">
        <h2 id="dialog-title">{title}</h2>
        {children}
        <div className="actions">
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            {t.common.cancel}
          </button>
          <button type="button" className="btn btn-danger-solid" disabled={busy} onClick={onConfirm}>
            {confirm}
          </button>
        </div>
      </div>
    </dialog>
  );
}

export function LoadError({ onRetry }: { onRetry: () => void }) {
  const { t } = useI18n();
  return (
    <div className="note neutral" role="alert">
      <span>
        {t.common.loadFailed}{' '}
        <button type="button" className="btn btn-sm btn-secondary" onClick={onRetry}>
          {t.common.retry}
        </button>
      </span>
    </div>
  );
}

export function Skeleton({ height }: { height: number }) {
  const { t } = useI18n();
  return <div className="skeleton" style={{ height }} role="status" aria-label={t.common.loading} />;
}

const icon = (path: ReactNode, size = 18) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" aria-hidden="true">
    {path}
  </svg>
);

export const Icons = {
  clocks: () => icon(<><rect x="3" y="4" width="18" height="16" rx="3" /><path d="M7 12h10" /></>),
  add: (size?: number) => icon(<path d="M12 5v14M5 12h14" />, size),
  history: () => icon(<path d="M4 12a8 8 0 1 0 3-6.2M4 4v4h4M12 8v4l3 2" />),
  profile: () => icon(<><circle cx="12" cy="8" r="4" /><path d="M4 20c1.5-4 4.5-6 8-6s6.5 2 8 6" /></>),
  settings: (size = 20) =>
    icon(<><path d="M4 7h10M18 7h2M4 17h2M10 17h10" /><circle cx="16" cy="7" r="2" /><circle cx="8" cy="17" r="2" /></>, size),
  lock: () => icon(<><rect x="5" y="10" width="14" height="10" rx="2" /><path d="M8 10V7a4 4 0 0 1 8 0v3" /></>),
  offline: () => icon(<path d="M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M12 19.5h.01M3 3l18 18" />),
};
