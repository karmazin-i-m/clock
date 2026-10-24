import type { Forms } from './plural';

// The source dictionary: en.ts must have exactly these keys. Strings are the design's own
// (design/Cabinet-*.dc.html); a function where a value is spliced in or a word is counted.
export const uk = {
  locale: 'uk-UA',

  nav: {
    label: 'Кабінет',
    clocks: 'Мої годинники',
    add: 'Додати годинник',
    history: 'Колишні годинники',
    profile: 'Профіль',
  },

  common: {
    online: 'Онлайн',
    offline: 'Офлайн',
    readonly: 'Лише перегляд',
    cancel: 'Скасувати',
    save: 'Зберегти зміни',
    saved: 'Збережено',
    copy: 'Скопіювати',
    copied: 'Скопійовано',
    loading: 'Завантаження…',
    retry: 'Спробувати ще',
    loadFailed: 'Не вдалося завантажити дані.',
    noSensor: 'немає датчика',
    unnamed: 'Годинник',
  },

  units: { mm: 'мм', mmHg: 'мм рт. ст.', percent: '%', celsius: '°C' },

  trend: { up: 'росте', down: 'падає', flat: 'стабільний' } as Record<string, string>,

  forms: {
    clocks: ['годинник', 'годинники', 'годинників'] as Forms,
    minutes: ['хвилину', 'хвилини', 'хвилин'] as Forms,
    hours: ['годину', 'години', 'годин'] as Forms,
    days: ['день', 'дні', 'днів'] as Forms,
  },

  time: {
    justNow: 'щойно',
    minAgo: (n: number) => `${n} хв тому`,
    hoursAgo: (n: number, word: string) => `${n} ${word} тому`,
    daysAgo: (n: number, word: string) => `${n} ${word} тому`,
    updated: (ago: string) => `оновлено ${ago}`,
    lastHeard: (ago: string) => `остання звістка ${ago}`,
    never: 'ще не звітував',
  },

  login: {
    title: 'Вхід у K-Clock',
    lead: 'Один акаунт — усі годинники: спальня, кухня, дача, офіс.',
    tagline: 'Історія показань і всі ваші годинники — в одному місці.',
    offlineNote: 'Годинник працює і без акаунта. Акаунт потрібен лише для хмари.',
    google: 'Увійти через Google',
    points: ['Без паролів і реєстраційних форм', 'Бачимо лише ваше ім’я та email', 'Акаунт можна видалити будь-коли'],
  },

  clocks: {
    title: 'Мої годинники',
    refresh: 'оновлюється щохвилини',
    summary: (count: string, online: number, total: number) =>
      total === 0
        ? count
        : online === total
          ? `${count} · ${total === 1 ? 'онлайн' : total === 2 ? 'обидва онлайн' : 'усі онлайн'}`
          : online === 0
            ? `${count} · ${total === 1 ? 'офлайн' : 'усі офлайн'}`
            : `${count} · ${online} онлайн`,
    temperature: 'Температура',
    pressure: 'Тиск',
    humidity: 'Вологість',
    dayChart: 'Температура за добу',
    history: 'Історія',
    settingsAria: (name: string) => `Налаштування годинника «${name}»`,
    openAria: (name: string) => `Показання годинника «${name}»`,
    addTitle: 'Додати годинник',
    addHintOne: 'Наприклад, у дитячу чи на кухню — і порівнюйте мікроклімат кімнат.',
    addHintMore: 'Ще одна кімната, дача чи офіс. Кожен годинник прив’язується одноразовим кодом.',
    former: (n: number) => `Колишні годинники · ${n} — історія тих, що ви відв’язали або передали`,
    emptyTitle: 'У вас ще немає годинників',
    emptyText: 'Підключіть K-Clock до WiFi, а потім прив’яжіть його до акаунта одноразовим кодом. Це займе кілька хвилин.',
  },

  clock: {
    tabs: 'Розділи годинника',
    readings: 'Показання',
    settings: 'Налаштування',
    screen: 'Екран дисплея',
    screens: { time: 'Час', temp: 'Температура', press: 'Тиск', hum: 'Вологість' },
    dayRange: (min: string, max: string) => `за добу ${min} – ${max}`,
    trendLine: (arrow: string, word: string, delta: string) => `${arrow} ${word} · ${delta} за 3 год`,
    updated: 'Оновлено',
    next: (ago: string, next: string) => `${ago} · наступне ~${next}`,
    history: 'Історія',
    period: 'Період',
    periods: { day: 'День', week: 'Тиждень', month: 'Місяць', year: 'Рік' },
    lastDay: 'останні 24 години',
    tempChart: 'Температура, °C',
    pressChart: 'Тиск, мм рт. ст.',
    humChart: 'Вологість, %',
    gapsNote: 'Заштриховані проміжки — годинник у цей час не звітував (немає живлення або WiFi).',
    noHumidityTitle: 'Вологість',
    noHumidity: 'У цьому годиннику датчик BMP280: він міряє температуру й тиск, але не вологість. Графіка вологості не буде.',
    offlineTitle: (name: string, since: string) => `${name} не на зв’язку з ${since}.`,
    offlineText: 'Показуємо останні відомі значення. Сам годинник, найімовірніше, працює — перевірте живлення і WiFi.',
    notFound: 'Такого годинника немає серед ваших.',
    noData: 'Ще немає показань.',
  },

  chart: {
    noData: 'немає даних',
    now: 'зараз',
    aria: (name: string, min: string, max: string, gaps: boolean) =>
      `Графік: ${name}, від ${min} до ${max}${gaps ? ', з прогалинами без даних' : ''}`,
    empty: 'За цей період показань немає.',
    names: { temp: 'температура', pressure: 'тиск', humidity: 'вологість' } as Record<string, string>,
  },

  settings: {
    general: 'Загальне',
    name: 'Назва',
    timezone: 'Часовий пояс',
    display: 'Дисплей',
    brightnessNote: 'Яскравість має лише два рівні — звичайний і нічний.',
    night: 'Нічне притлумлення',
    never: 'ніколи',
    always: 'цілодобово',
    presetNever: 'Ніколи',
    presetAlways: 'Цілодобово',
    from: 'З',
    to: 'До',
    fromEarlier: 'Початок на годину раніше',
    fromLater: 'Початок на годину пізніше',
    toEarlier: 'Кінець на годину раніше',
    toLater: 'Кінець на годину пізніше',
    hoursShort: 'год',
    marquee: 'Біжучий рядок',
    marqueeGroup: 'Період біжучого рядка',
    marqueeOff: 'Вимк',
    marqueeOffSummary: 'вимкнено',
    marqueeEvery: (n: number) => `кожні ${n} хв`,
    marqueeMin: (n: number) => `${n} хв`,
    marqueeNote: 'Як часто всі екрани пропливають по матриці. «Вимк» — годинник показує лише поточний екран.',
    saveNote: 'Годинник отримає їх з наступним пакетом, приблизно за хвилину.',
    pending: 'Годинник ще не підтвердив останні зміни.',
    tech: 'Технічне',
    firmware: 'Прошивка',
    sensor: 'Датчик',
    lastContact: 'Останній зв’язок',
    readings: 'Показань у хмарі',
    linked: 'Прив’язано',
    address: 'Адреса в мережі',
    unbindTitle: 'Відв’язати годинник',
    unbindText:
      'Якщо продаєте або передаєте годинник. Він продовжить працювати, а ваша історія залишиться у вас у «Колишніх годинниках» — новий власник її не побачить.',
    unbindButton: 'Відв’язати…',
    unbindConfirmTitle: (name: string) => `Відв’язати «${name}»?`,
    unbindConfirmText:
      'Годинник продовжить працювати, але зникне з ваших годинників. Ваша історія залишиться у вас — новий власник її не побачить.',
    unbindConfirm: 'Відв’язати',
    nameInvalid: 'Назва — від 1 до 40 символів.',
    saveFailed: 'Не вдалося зберегти.',
  },

  add: {
    title: 'Додати годинник',
    lead: 'Годинник уже має бути підключений до вашого WiFi. Прив’язка — одноразовим кодом.',
    step1: 'Отримайте код',
    step1Text: 'Код діє 15 хвилин і спрацює лише один раз.',
    getCode: 'Отримати код',
    step2: 'Ваш код прив’язки',
    left: (mmss: string) => `залишилось ${mmss}`,
    validity: 'Час дії коду',
    step3: 'Введіть код на годиннику',
    step3Before: 'У тій самій WiFi-мережі відкрийте ',
    step3After: ', вставте код у поле «Код прив’язки з акаунта» і натисніть «Зберегти». Годинник приймає код і з дефісом, і без.',
    step4: 'Чекаємо на годинник…',
    step4Text: 'Картка з’явиться в «Моїх годинниках», щойно годинник обміняє код — зазвичай до хвилини.',
    preview: 'Так виглядає сторінка годинника',
    previewTitle: 'Налаштування',
    previewWifi: 'Домашня мережа · підключено',
    previewField: 'Код прив’язки з акаунта',
    previewSave: 'Зберегти',
    status: { active: 'Активний', used: 'Використаний', expired: 'Прострочений' } as Record<string, string>,
    usedText: 'Годинник прив’язано.',
    open: 'Відкрити',
    expiredTitle: 'Код прострочений',
    expiredText: 'Код діє 15 хвилин. Отримайте новий і введіть його на сторінці годинника.',
    newCode: 'Отримати новий код',
    createFailed: 'Не вдалося отримати код.',
  },

  history: {
    title: 'Колишні годинники',
    lead: 'Історія годинників, які ви відв’язали або передали. Дані належать вам; керувати цими годинниками вже не можна.',
    reason: {
      'owner requested': 'Відв’язано вами',
      're-enrolled': 'Передано іншому власнику',
      'account deleted': 'Акаунт видалено',
    } as Record<string, string>,
    reasonOther: 'Відв’язано',
    span: (from: string, to: string) => `Дані з ${from} до ${to}`,
    note: 'Годинник відв’язано. Нові показання сюди не надходять, а новий власник не бачить цієї історії.',
    empty: 'Колишніх годинників немає. Тут з’являться ті, що ви відв’яжете або передасте.',
    notFound: 'Такого колишнього годинника немає.',
  },

  profile: {
    title: 'Профіль',
    viaGoogle: 'вхід через Google',
    signOut: 'Вийти',
    clocks: 'Годинників',
    former: 'Колишніх',
    since: 'У K-Clock з',
    ui: 'Інтерфейс',
    language: 'Мова',
    theme: 'Тема',
    themes: { dark: 'Темна', light: 'Світла', system: 'Як у системі' },
    deleteTitle: 'Видалити акаунт',
    deleteText:
      'Усі показання всіх ваших годинників, разом з історією колишніх, буде стерто безповоротно. Самі годинники продовжать працювати без хмари.',
    deleteWord: 'ВИДАЛИТИ',
    deleteLabelBefore: 'Щоб підтвердити, введіть ',
    deleteButton: 'Видалити акаунт і всі дані',
    deleteFailed: 'Не вдалося видалити акаунт.',
  },

  notFound: { title: 'Сторінки не знайдено', back: 'До моїх годинників' },
};

export type Dictionary = typeof uk;
