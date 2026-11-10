import type { ReactNode } from "react";
import { Link, NavLink, useLocation } from "react-router-dom";
import { useAuth } from "../state/AuthContext";
import { AccountsIcon, BudgetsIcon, CalendarIcon, CategoriesIcon, DashboardIcon, GoalsIcon, ImportsIcon, ReportsIcon, SettingsIcon, TransactionsIcon } from "./icons";
import { useI18n } from "../state/I18nContext";
import { useMonthSelection } from "../state/MonthContext";

export function AppShell({ children }: { children: ReactNode }) {
  const location = useLocation();
  const { auth, logout } = useAuth();
  const { languageCode, t } = useI18n();
  const { selectedMonth, setSelectedMonth, goToPreviousMonth, goToNextMonth, goToCurrentMonth } = useMonthSelection();
  const isSettingsRoute = location.pathname.startsWith("/settings");
  const signedInIdentity = auth?.email || auth?.login || t("common.unknown");
  const hasValidSelectedMonth = /^\d{4}-(0[1-9]|1[0-2])$/.test(selectedMonth);
  const selectedMonthDate = hasValidSelectedMonth ? new Date(`${selectedMonth}-01T00:00:00.000Z`) : null;
  const selectedMonthLabel = selectedMonthDate && !Number.isNaN(selectedMonthDate.getTime())
    ? new Intl.DateTimeFormat(languageCode, {
      month: "long",
      year: "numeric",
      timeZone: "UTC"
    }).format(selectedMonthDate)
    : "";
  const navItems = [
    { to: "/dashboard", label: t("nav.dashboard"), icon: DashboardIcon },
    { to: "/insights", label: "Insights", icon: ReportsIcon },
    { to: "/reports", label: t("nav.reports"), icon: ReportsIcon },
    { to: "/transactions", label: t("nav.transactions"), icon: TransactionsIcon },
    { to: "/recurring", label: t("nav.recurring"), icon: CalendarIcon },
    { to: "/imports", label: t("nav.imports"), icon: ImportsIcon },
    { to: "/bookkeeping", label: "Bookkeeping", icon: AccountsIcon },
    { to: "/accounts", label: t("nav.accounts"), icon: AccountsIcon },
    { to: "/budgets", label: t("nav.budgets"), icon: BudgetsIcon },
    { to: "/goals", label: t("nav.goals"), icon: GoalsIcon },
    { to: "/categories", label: t("nav.categories"), icon: CategoriesIcon },
    { to: "/settings", label: t("nav.settings"), icon: SettingsIcon }
  ];
  const workspaceNavItems = navItems.filter((item) => item.to !== "/settings");
  const systemNavItems = navItems.filter((item) => item.to === "/settings");

  const renderNavLink = (item: (typeof navItems)[number]) => (
    <NavLink
      key={item.to}
      to={item.to}
      className={({ isActive }) => isActive ? "nav-link active" : "nav-link"}
    >
      <span className="nav-icon">
        <item.icon />
      </span>
      <span>{item.label}</span>
    </NavLink>
  );

  return (
    <div className="shell">
      <aside className="sidebar">
        <Link to="/dashboard" className="brand">
          <span className="brand-mark">L</span>
          <div>
            <strong>Ledgerra</strong>
            <p>{t("appShell.brandTagline")}</p>
          </div>
        </Link>

        <nav className="nav">
          <div className="nav-group">
            <span className="nav-group-label">{t("appShell.workspace")}</span>
            {workspaceNavItems.map(renderNavLink)}
          </div>
          <div className="nav-group">
            <span className="nav-group-label">{t("appShell.system")}</span>
            {systemNavItems.map(renderNavLink)}
          </div>
        </nav>

        <div className="month-panel">
          <label htmlFor="global-month">{t("appShell.month")}</label>
          <strong className="month-selected-label">{selectedMonthLabel}</strong>
          <div className="month-controls">
            <button className="ghost-button compact-button" type="button" onClick={goToPreviousMonth} aria-label={t("appShell.previousMonth")}>
              &lt;
            </button>
            <input
              id="global-month"
              type="month"
              value={selectedMonth}
              onChange={(event) => setSelectedMonth(event.target.value)}
            />
            <button className="ghost-button compact-button" type="button" onClick={goToNextMonth} aria-label={t("appShell.nextMonth")}>
              &gt;
            </button>
          </div>
          <button className="ghost-button month-today-button" type="button" onClick={goToCurrentMonth}>
            {t("appShell.currentMonth")}
          </button>
        </div>

        <div className="sidebar-footer account-panel">
          <div>
            <small>{t("appShell.signedInAs")}</small>
            <strong title={signedInIdentity}>{signedInIdentity}</strong>
          </div>
          <button className="ghost-button" onClick={logout}>
            {t("appShell.signOut")}
          </button>
        </div>
      </aside>

      <main className={isSettingsRoute ? "content content--settings" : "content"}>{children}</main>

      <nav className="mobile-nav" aria-label={t("appShell.navigation")} >
        {navItems.map((item) => (
          <NavLink
            key={`mobile-${item.to}`}
            to={item.to}
            className={({ isActive }) => isActive ? "mobile-nav-link active" : "mobile-nav-link"}
          >
            <item.icon />
            <span>{item.label}</span>
          </NavLink>
        ))}
      </nav>
    </div>
  );
}
