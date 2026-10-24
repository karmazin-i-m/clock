import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { createCode, useCodes } from '../api/queries';
import type { CreatedCode } from '../api/types';
import { useI18n } from '../i18n';
import { mmss } from '../lib/format';
import { useNow } from '../lib/useNow';

const TTL_MS = 15 * 60_000;
const STORE = 'kclock.pendingCode';

// The plaintext code comes back from the server exactly once, so the tab keeps it: a reload
// in the middle of typing it into the clock must not lose it. sessionStorage, not local —
// it is a short-lived secret and should not outlive the tab.
function loadPending(): CreatedCode | null {
  try {
    const raw = sessionStorage.getItem(STORE);
    return raw ? (JSON.parse(raw) as CreatedCode) : null;
  } catch {
    return null;
  }
}

function storePending(code: CreatedCode | null) {
  try {
    if (code) sessionStorage.setItem(STORE, JSON.stringify(code));
    else sessionStorage.removeItem(STORE);
  } catch {
    // The code still shows; it just will not survive a reload.
  }
}

function Step({ n, state, title, children }: { n: number; state: 'done' | 'current' | 'next'; title: string; children: React.ReactNode }) {
  return (
    <li className={`step step-${state}`} aria-current={state === 'current' ? 'step' : undefined}>
      <span className="step-mark mono" aria-hidden="true">
        {state === 'done' ? '✓' : n}
      </span>
      <div className="step-body">
        <h2>{title}</h2>
        {children}
      </div>
    </li>
  );
}

export function Add() {
  const { t } = useI18n();
  const client = useQueryClient();
  const [code, setCode] = useState<CreatedCode | null>(loadPending);
  const [creating, setCreating] = useState(false);
  const [failed, setFailed] = useState(false);
  const [copied, setCopied] = useState(false);
  const now = useNow(1000);

  const expiresAt = code ? Date.parse(code.expires_at) : 0;
  const expiredLocally = code !== null && now >= expiresAt;
  const codes = useCodes(code !== null && !expiredLocally);
  const listed = code ? codes.data?.find((c) => c.id === code.id) : undefined;
  const status = expiredLocally ? 'expired' : (listed?.status ?? 'active');

  useEffect(() => {
    if (status === 'used') {
      storePending(null);
      void client.invalidateQueries({ queryKey: ['devices'] });
    }
  }, [status, client]);

  const getCode = async () => {
    setCreating(true);
    setFailed(false);
    try {
      const created = await createCode();
      storePending(created);
      setCode(created);
      setCopied(false);
    } catch {
      setFailed(true);
    } finally {
      setCreating(false);
    }
  };

  const copy = async () => {
    if (!code) return;
    try {
      await navigator.clipboard.writeText(code.code);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  const left = Math.max(0, expiresAt - now);
  const steps: ('done' | 'current' | 'next')[] = !code
    ? ['current', 'next', 'next', 'next']
    : status === 'used'
      ? ['done', 'done', 'done', 'done']
      : ['done', 'current', 'next', 'next'];

  return (
    <main className="main">
      <div className="titles page-titles">
        <h1>{t.add.title}</h1>
        <p className="lead">{t.add.lead}</p>
      </div>

      <div className="add-layout">
        <ol className="steps">
          <Step n={1} state={steps[0]!} title={t.add.step1}>
            <p className="muted">{t.add.step1Text}</p>
            {!code && (
              <div>
                <button type="button" className="btn btn-primary" disabled={creating} onClick={getCode}>
                  {t.add.getCode}
                </button>
                {failed && <p className="field-error">{t.add.createFailed}</p>}
              </div>
            )}
          </Step>

          {code && status === 'active' && (
            <Step n={2} state="current" title={t.add.step2}>
              <div className="code-box">
                <span className="code mono">{code.code}</span>
                <button type="button" className="btn btn-quiet" onClick={copy}>
                  {copied ? t.common.copied : t.common.copy}
                </button>
              </div>
              <div className="code-timer">
                <div className="code-timer-row">
                  <span className="pill pill-active">{t.add.status.active}</span>
                  <span className="mono muted">{t.add.left(mmss(left))}</span>
                </div>
                <div
                  role="progressbar"
                  aria-label={t.add.validity}
                  aria-valuemin={0}
                  aria-valuemax={TTL_MS / 1000}
                  aria-valuenow={Math.round(left / 1000)}
                  className="progress"
                >
                  <div style={{ width: `${(left / TTL_MS) * 100}%` }} />
                </div>
              </div>
            </Step>
          )}

          {code && status === 'used' && (
            <li className="step step-done code-state">
              <span className="pill pill-used">{t.add.status.used}</span>
              <span className="code mono struck">{code.code}</span>
              <span className="muted">
                {t.add.usedText}{' '}
                {listed?.consumed_device_id != null && (
                  <Link className="text-link" to={`/clocks/${listed.consumed_device_id}`}>
                    {t.add.open}
                  </Link>
                )}
              </span>
            </li>
          )}

          {code && status === 'expired' && (
            <li className="step code-state expired" role="alert">
              <span className="pill pill-expired">{t.add.status.expired}</span>
              <span className="expired-title">{t.add.expiredTitle}</span>
              <span className="muted">{t.add.expiredText}</span>
              <button type="button" className="btn btn-primary btn-sm" disabled={creating} onClick={getCode}>
                {t.add.newCode}
              </button>
            </li>
          )}

          <Step n={3} state={status === 'used' ? 'done' : 'next'} title={t.add.step3}>
            <p className="muted">
              {t.add.step3Before}
              <span className="mono strong">k-clock.local/settings</span>
              {t.add.step3After}
            </p>
          </Step>
          <Step n={4} state={status === 'used' ? 'done' : 'next'} title={t.add.step4}>
            <p className="muted">{t.add.step4Text}</p>
          </Step>
        </ol>

        <aside className="device-preview" aria-label={t.add.preview}>
          <span className="small muted">{t.add.preview}</span>
          <div className="preview-window" lang="uk">
            <div className="preview-bar mono">
              <span aria-hidden="true" />
              k-clock.local/settings
            </div>
            <div className="preview-body">
              <span className="preview-title">{t.add.previewTitle}</span>
              <div className="preview-field">
                <span className="small">WiFi</span>
                <span>{t.add.previewWifi}</span>
              </div>
              <div className="preview-field">
                <span className="small strong">{t.add.previewField}</span>
                <div className="preview-input mono">{code?.code ?? 'XXXX-XXXX'}</div>
              </div>
              <div className="preview-save">{t.add.previewSave}</div>
            </div>
          </div>
        </aside>
      </div>
    </main>
  );
}
