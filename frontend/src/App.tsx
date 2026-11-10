import { InsightsPage } from "./pages/InsightsPage";
import { BookkeepingPage } from "./pages/BookkeepingPage";
import { Suspense, lazy } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { AppShell } from "./ui/AppShell";
import { useAuth } from "./state/AuthContext";
import { MonthProvider } from "./state/MonthContext";
import { AccountsPage } from "./pages/AccountsPage";
import { BudgetsPage } from "./pages/BudgetsPage";
import { CategoriesPage } from "./pages/CategoriesPage";
import { DashboardPage } from "./pages/DashboardPage";
import { ImportsPage } from "./pages/ImportsPage";
import { RecurringTransactionsPage } from "./pages/RecurringTransactionsPage";
import { GoalsPage } from "./pages/GoalsPage";
import { LoginPage } from "./pages/LoginPage";
import { SettingsPage } from "./pages/SettingsPage";
import { TransactionsPage } from "./pages/TransactionsPage";
import { ImportAnalysisProvider } from "./state/ImportAnalysisContext";
import { I18nProvider } from "./state/I18nContext";

const ReportsPage = lazy(async () => {
  const module = await import("./pages/ReportsPage");
  return { default: module.ReportsPage };
});

function ProtectedRoutes() {
  return (
    <MonthProvider>
      <ImportAnalysisProvider>
        <AppShell>
          <Suspense fallback={<div className="page-stack"><p>Loading...</p></div>}>
            <Routes>
              <Route path="/" element={<Navigate to="/dashboard" replace />} />
              <Route path="/dashboard" element={<DashboardPage />} />
              <Route path="/insights" element={<InsightsPage />} />
              <Route path="/reports" element={<ReportsPage />} />
              <Route path="/transactions" element={<TransactionsPage />} />
              <Route path="/recurring" element={<RecurringTransactionsPage />} />
              <Route path="/imports" element={<ImportsPage />} />
              <Route path="/bookkeeping" element={<BookkeepingPage />} />
              <Route path="/accounts" element={<AccountsPage />} />
              <Route path="/accounts/:accountId" element={<AccountsPage />} />
              <Route path="/budgets" element={<BudgetsPage />} />
              <Route path="/goals" element={<GoalsPage />} />
              <Route path="/categories" element={<CategoriesPage />} />
              <Route path="/settings" element={<SettingsPage />} />
            </Routes>
          </Suspense>
        </AppShell>
      </ImportAnalysisProvider>
    </MonthProvider>
  );
}

export function App() {
  const { isAuthenticated, isRestoring } = useAuth();

  return (
    <I18nProvider>
      {isRestoring ? (
        <div className="page-stack"><p>Loading...</p></div>
      ) : (
        <Routes>
          <Route path="/login" element={isAuthenticated ? <Navigate to="/dashboard" replace /> : <LoginPage />} />
          <Route path="/*" element={isAuthenticated ? <ProtectedRoutes /> : <Navigate to="/login" replace />} />
        </Routes>
      )}
    </I18nProvider>
  );
}
