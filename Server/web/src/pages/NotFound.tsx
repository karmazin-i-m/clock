import { Link } from 'react-router-dom';
import { useI18n } from '../i18n';
import { Led } from '../led/Led';

export function NotFound() {
  const { t } = useI18n();
  return (
    <main className="main">
      <div className="card empty">
        <Led text="-404-" pixel={8} mode="night" label="404" />
        <h1>{t.notFound.title}</h1>
        <Link to="/clocks" className="btn btn-primary">
          {t.notFound.back}
        </Link>
      </div>
    </main>
  );
}
