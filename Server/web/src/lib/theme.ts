import { useEffect, useState } from 'react';
import { readPref, writePref } from '../i18n';

export type ThemePref = 'dark' | 'light' | 'system';

const QUERY = '(prefers-color-scheme: light)';

function apply(pref: ThemePref) {
  const light = pref === 'light' || (pref === 'system' && matchMedia(QUERY).matches);
  document.documentElement.dataset.theme = light ? 'light' : 'dark';
}

/** The profile's theme choice. index.html applies the stored one before React starts. */
export function useTheme() {
  const [pref, setPref] = useState<ThemePref>(() => readPref('kclock.theme', ['dark', 'light', 'system'], 'dark'));

  useEffect(() => {
    apply(pref);
    if (pref !== 'system') return;
    const list = matchMedia(QUERY);
    const update = () => apply('system');
    list.addEventListener('change', update);
    return () => list.removeEventListener('change', update);
  }, [pref]);

  return [
    pref,
    (next: ThemePref) => {
      writePref('kclock.theme', next);
      setPref(next);
    },
  ] as const;
}
