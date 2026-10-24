import { useEffect, useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useDevice, useSaveClock, useSettings, useSummary, useUnbind } from '../api/queries';
import { useI18n } from '../i18n';
import { date, dateTime } from '../lib/format';
import { timezoneOptions } from '../lib/timezones';
import { NightSlider } from '../night/NightSlider';
import type { Night } from '../night/model';
import { ConfirmDialog, LoadError, Skeleton } from '../ui/components';
import { ClockHeader, NotFoundNote } from './Clock';
import { clockName } from './clockView';

const MARQUEE_CHOICES = [0, 2, 5, 10, 15] as const;
const NAME_MAX = 40;

export function ClockSettings() {
  const id = Number(useParams().id);
  const { t, lang } = useI18n();
  const navigate = useNavigate();
  const device = useDevice(id);
  const settings = useSettings(id);
  const summary = useSummary(id);
  const save = useSaveClock(id);
  const unbind = useUnbind(id);

  const [name, setName] = useState('');
  const [timezone, setTimezone] = useState('');
  const [night, setNight] = useState<Night>({ from: 22, until: 7 });
  const [marquee, setMarquee] = useState(5);
  const [confirming, setConfirming] = useState(false);
  const [saved, setSaved] = useState(false);

  // The form starts from the server's values once, and is not overwritten by the minute's
  // refetch while someone is editing it.
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    if (loaded || !device.data || !settings.data) return;
    setName(device.data.display_name ?? '');
    setTimezone(device.data.timezone);
    setNight({ from: settings.data.dim_from, until: settings.data.dim_until });
    setMarquee(settings.data.marquee_minutes);
    setLoaded(true);
  }, [loaded, device.data, settings.data]);

  if (device.isPending || (settings.isPending && !settings.isError)) {
    return (
      <main className="main narrow">
        <Skeleton height={600} />
      </main>
    );
  }
  if (device.error instanceof ApiError && device.error.status === 404) {
    return (
      <main className="main narrow">
        <NotFoundNote text={t.clock.notFound} />
      </main>
    );
  }
  if (device.isError || settings.isError) {
    return (
      <main className="main narrow">
        <LoadError onRetry={() => (device.refetch(), settings.refetch())} />
      </main>
    );
  }

  const d = device.data;
  const s = settings.data;
  const trimmed = name.trim();
  // A clock nobody has named yet may stay unnamed; a name, once typed, must be a real one.
  const nameValid = trimmed === (d.display_name ?? '') || (trimmed.length > 0 && trimmed.length <= NAME_MAX);

  const details: { display_name?: string; timezone?: string } = {};
  if (trimmed !== (d.display_name ?? '')) details.display_name = trimmed;
  if (timezone !== d.timezone) details.timezone = timezone;
  const displayChanged = night.from !== s.dim_from || night.until !== s.dim_until || marquee !== s.marquee_minutes;
  const dirty = Object.keys(details).length > 0 || displayChanged;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!nameValid || !dirty) return;
    setSaved(false);
    save.mutate(
      {
        details: Object.keys(details).length > 0 ? details : undefined,
        display: displayChanged ? { dim_from: night.from, dim_until: night.until, marquee_minutes: marquee } : undefined,
      },
      { onSuccess: () => setSaved(true) },
    );
  };

  const pending = s.applied_version !== null && s.applied_version !== s.version;
  const tech = [
    { k: t.settings.firmware, v: d.fw_version ?? '—' },
    { k: t.settings.sensor, v: d.model?.toUpperCase() ?? '—' },
    { k: t.settings.lastContact, v: d.last_seen_at ? dateTime(t, d.last_seen_at, d.timezone) : t.time.never },
    { k: t.settings.readings, v: summary.data ? new Intl.NumberFormat(t.locale).format(summary.data.readings) : '—' },
    { k: t.settings.linked, v: date(t, d.bound_at, d.timezone) },
    { k: t.settings.address, v: 'k-clock.local' },
  ];

  return (
    <main className="main narrow">
      <ClockHeader device={d} current="settings" />

      <form className="settings-form" onSubmit={submit} noValidate>
        <section className="card" aria-labelledby="s-general">
          <h2 id="s-general">{t.settings.general}</h2>
          <div className="form-grid">
            <div className="field">
              <label htmlFor="cs-name">{t.settings.name}</label>
              <input
                id="cs-name"
                className="input"
                value={name}
                maxLength={NAME_MAX}
                aria-invalid={!nameValid}
                aria-describedby={nameValid ? undefined : 'cs-name-error'}
                onChange={(e) => setName(e.target.value)}
              />
              {!nameValid && (
                <span id="cs-name-error" className="field-error">
                  {t.settings.nameInvalid}
                </span>
              )}
            </div>
            <div className="field">
              <label htmlFor="cs-tz">{t.settings.timezone}</label>
              <select id="cs-tz" className="input" value={timezone} onChange={(e) => setTimezone(e.target.value)}>
                {timezoneOptions(lang, d.timezone).map((z) => (
                  <option key={z.id} value={z.id}>
                    {z.label}
                  </option>
                ))}
              </select>
            </div>
          </div>
        </section>

        <section className="card" aria-labelledby="s-display">
          <h2 id="s-display">{t.settings.display}</h2>
          <NightSlider value={night} onChange={setNight} />
          <p className="small muted">{t.settings.brightnessNote}</p>
          <div className="marquee-block">
            <div className="night-head">
              <span className="night-title">{t.settings.marquee}</span>
              <span className="mono night-summary">{marquee === 0 ? t.settings.marqueeOffSummary : t.settings.marqueeEvery(marquee)}</span>
            </div>
            <div className="chips" role="group" aria-label={t.settings.marqueeGroup}>
              {MARQUEE_CHOICES.map((m) => (
                <button key={m} type="button" className="chip square" aria-pressed={m === marquee} onClick={() => setMarquee(m)}>
                  {m === 0 ? t.settings.marqueeOff : t.settings.marqueeMin(m)}
                </button>
              ))}
            </div>
            <span className="small muted">{t.settings.marqueeNote}</span>
          </div>
        </section>

        <div className="save-row">
          <button type="submit" className="btn btn-primary" disabled={!dirty || !nameValid || save.isPending}>
            {t.common.save}
          </button>
          <span className="muted small" role="status">
            {save.isError ? (
              <span className="field-error">{t.settings.saveFailed}</span>
            ) : saved && !dirty ? (
              `${t.common.saved}. ${t.settings.saveNote}`
            ) : pending ? (
              t.settings.pending
            ) : (
              t.settings.saveNote
            )}
          </span>
        </div>
      </form>

      <section className="tech" aria-labelledby="s-tech">
        <h2 id="s-tech">{t.settings.tech}</h2>
        <dl className="tiles tech-tiles">
          {tech.map((r) => (
            <div key={r.k}>
              <dt>{r.k}</dt>
              <dd>{r.v}</dd>
            </div>
          ))}
        </dl>
      </section>

      <section className="card danger danger-row" aria-labelledby="s-danger">
        <div className="danger-text">
          <h2 id="s-danger">{t.settings.unbindTitle}</h2>
          <p>{t.settings.unbindText}</p>
        </div>
        <button type="button" className="btn btn-danger" onClick={() => setConfirming(true)}>
          {t.settings.unbindButton}
        </button>
      </section>

      <ConfirmDialog
        open={confirming}
        title={t.settings.unbindConfirmTitle(clockName(t, d))}
        confirm={t.settings.unbindConfirm}
        busy={unbind.isPending}
        onClose={() => setConfirming(false)}
        onConfirm={() => unbind.mutate(undefined, { onSuccess: () => navigate('/history') })}
      >
        <p>{t.settings.unbindConfirmText}</p>
      </ConfirmDialog>
    </main>
  );
}
