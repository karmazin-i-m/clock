import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { en } from './en';
import { plural, type Forms, type Lang } from './plural';
import { uk, type Dictionary } from './uk';

const dictionaries: Record<Lang, Dictionary> = { uk, en };

// Interface preferences are per browser, not per account (plan: no new columns for them), so
// they live in localStorage — guarded, because a private window may refuse it outright.
export function readPref<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
  try {
    const value = localStorage.getItem(key);
    return allowed.includes(value as T) ? (value as T) : fallback;
  } catch {
    return fallback;
  }
}

export function writePref(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Not remembered; the choice still holds until the page is closed.
  }
}

type I18n = {
  lang: Lang;
  t: Dictionary;
  setLang: (lang: Lang) => void;
  count: (n: number, forms: Forms) => string;
};

const Context = createContext<I18n | null>(null);

export function I18nProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(() => readPref('kclock.lang', ['uk', 'en'], 'uk'));

  useEffect(() => {
    document.documentElement.lang = lang;
  }, [lang]);

  const value: I18n = {
    lang,
    t: dictionaries[lang],
    setLang: (next) => {
      writePref('kclock.lang', next);
      setLangState(next);
    },
    count: (n, forms) => `${n} ${plural(lang, n, forms)}`,
  };
  return <Context.Provider value={value}>{children}</Context.Provider>;
}

export function useI18n(): I18n {
  const value = useContext(Context);
  if (!value) throw new Error('useI18n outside I18nProvider');
  return value;
}
