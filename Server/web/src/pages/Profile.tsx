import { useState } from 'react';
import { deleteAccount, signOut, useDevices, useHistory, useMe } from '../api/queries';
import { useI18n } from '../i18n';
import type { Lang } from '../i18n/plural';
import { monthYear } from '../lib/format';
import { useTheme, type ThemePref } from '../lib/theme';
import { LoadError, Segmented, Skeleton } from '../ui/components';

export function Profile() {
  const { t, lang, setLang } = useI18n();
  const [theme, setTheme] = useTheme();
  const me = useMe();
  const devices = useDevices();
  const history = useHistory();
  const [word, setWord] = useState('');
  const [deleting, setDeleting] = useState(false);
  const [deleteFailed, setDeleteFailed] = useState(false);

  // Leaving is a full page load, not a route change: nothing cached for this account may
  // survive into whatever happens next in this tab.
  const leave = async (action: () => Promise<void>) => {
    await action();
    location.assign('/login');
  };

  const remove = async () => {
    setDeleting(true);
    setDeleteFailed(false);
    try {
      await leave(deleteAccount);
    } catch {
      setDeleteFailed(true);
      setDeleting(false);
    }
  };

  if (me.isPending) {
    return (
      <main className="main slim">
        <Skeleton height={400} />
      </main>
    );
  }
  if (me.isError) {
    return (
      <main className="main slim">
        <LoadError onRetry={() => me.refetch()} />
      </main>
    );
  }

  const m = me.data;
  const name = m.display_name || m.email || '';
  const initial = name.trim().charAt(0).toUpperCase() || '·';

  return (
    <main className="main slim">
      <h1>{t.profile.title}</h1>

      <section className="card tight account-row">
        <div className="account">
          {m.picture ? (
            <img className="avatar" src={m.picture} alt="" referrerPolicy="no-referrer" />
          ) : (
            <div className="avatar" aria-hidden="true">
              {initial}
            </div>
          )}
          <div className="account-text">
            <span className="account-name">{m.display_name ?? m.email}</span>
            <span className="small muted">
              {m.email} · {t.profile.viaGoogle}
            </span>
          </div>
        </div>
        <button type="button" className="btn btn-secondary" onClick={() => void leave(signOut)}>
          {t.profile.signOut}
        </button>
      </section>

      <dl className="tiles stats">
        <div>
          <dt>{t.profile.clocks}</dt>
          <dd className="stat">{devices.data?.length ?? '—'}</dd>
        </div>
        <div>
          <dt>{t.profile.former}</dt>
          <dd className="stat">{history.data?.length ?? '—'}</dd>
        </div>
        <div>
          <dt>{t.profile.since}</dt>
          <dd className="stat">{monthYear(t, m.created_at)}</dd>
        </div>
      </dl>

      <section className="card tight" aria-labelledby="p-ui">
        <h2 id="p-ui" className="h3">
          {t.profile.ui}
        </h2>
        <div className="pref-row">
          <span>{t.profile.language}</span>
          <Segmented<Lang>
            label={t.profile.language}
            value={lang}
            options={[
              { id: 'uk', label: 'Українська' },
              { id: 'en', label: 'English' },
            ]}
            onChange={setLang}
          />
        </div>
        <div className="pref-row">
          <span>{t.profile.theme}</span>
          <Segmented<ThemePref>
            label={t.profile.theme}
            value={theme}
            options={[
              { id: 'dark', label: t.profile.themes.dark },
              { id: 'light', label: t.profile.themes.light },
              { id: 'system', label: t.profile.themes.system },
            ]}
            onChange={setTheme}
          />
        </div>
      </section>

      <section className="card tight danger" aria-labelledby="p-delete">
        <h2 id="p-delete" className="h3">
          {t.profile.deleteTitle}
        </h2>
        <p className="danger-copy">{t.profile.deleteText}</p>
        <div className="field delete-field">
          <label htmlFor="del-confirm">
            {t.profile.deleteLabelBefore}
            <span className="mono strong">{t.profile.deleteWord}</span>
          </label>
          <input
            id="del-confirm"
            className="input mono"
            autoComplete="off"
            placeholder={t.profile.deleteWord}
            value={word}
            onChange={(e) => setWord(e.target.value)}
          />
        </div>
        <button
          type="button"
          className="btn btn-danger-solid delete-btn"
          disabled={word.trim() !== t.profile.deleteWord || deleting}
          onClick={remove}
        >
          {t.profile.deleteButton}
        </button>
        {deleteFailed && (
          <p className="field-error" role="alert">
            {t.profile.deleteFailed}
          </p>
        )}
      </section>
    </main>
  );
}
