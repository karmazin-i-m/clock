import { Link, Outlet, useLocation } from 'react-router-dom';
import { useI18n } from '../i18n';
import { Logo } from '../led/Led';
import { Icons } from './components';

export function Layout() {
  const { t } = useI18n();
  const { pathname } = useLocation();

  // /clocks/add sits under /clocks in the URL but is its own menu item, so "My clocks" must
  // not also light up on it.
  const adding = pathname === '/clocks/add';
  const links = [
    { to: '/clocks', label: t.nav.clocks, icon: Icons.clocks(), current: pathname.startsWith('/clocks') && !adding },
    { to: '/clocks/add', label: t.nav.add, icon: Icons.add(), current: adding },
    { to: '/history', label: t.nav.history, icon: Icons.history(), current: pathname.startsWith('/history') },
    { to: '/profile', label: t.nav.profile, icon: Icons.profile(), current: pathname === '/profile' },
  ];

  return (
    <div className="shell">
      <nav className="sidebar" aria-label={t.nav.label}>
        <Link to="/clocks">
          <Logo />
        </Link>
        <div className="nav">
          {links.map((l) => (
            <Link key={l.to} to={l.to} aria-current={l.current ? 'page' : undefined}>
              {l.icon}
              {l.label}
            </Link>
          ))}
        </div>
      </nav>
      <Outlet />
    </div>
  );
}
