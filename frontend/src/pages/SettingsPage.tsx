import { ChangeEvent, FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { apiClient } from "../api/client";
import { useLedgerraData } from "../hooks/useLedgerraData";
import { useAuth } from "../state/AuthContext";
import { useI18n } from "../state/I18nContext";
import { accentPresets, useTheme, type AccentColor, type ThemePreference } from "../state/ThemeContext";
import type { ExchangeRate, ImportRule, PersonalAccessToken } from "../types";
import { AccountsIcon, CashFlowIcon, CategoryIcon, ImportsIcon, ReportsIcon, SettingsIcon } from "../ui/icons";
import { ActionMenu } from "../ui/ActionMenu";
import { SectionCard } from "../ui/SectionCard";
import { normalizeCurrencyCode, supportedCurrencies } from "../utils/currency";
import { normalizeLanguageCode, supportedLanguages } from "../utils/language";

type SettingsSection = "appearance" | "region" | "profile" | "security" | "ai" | "rules" | "backup" | "danger";
type DensityPreference = "comfortable" | "standard" | "compact";

const densityStorageKey = "ledgerra:density";
const animationStorageKey = "ledgerra:animations";
const minimalNavigationStorageKey = "ledgerra:minimal-navigation";

function getErrorMessage(exception: unknown, fallback: string) {
  return exception instanceof Error ? exception.message : fallback;
}

function resolveInitialDensityPreference(): DensityPreference {
  if (typeof window === "undefined") {
    return "standard";
  }

  const storedDensityPreference = window.localStorage.getItem(densityStorageKey);
  return storedDensityPreference === "comfortable" || storedDensityPreference === "compact" || storedDensityPreference === "standard"
    ? storedDensityPreference
    : "standard";
}

function resolveInitialBooleanPreference(storageKey: string, fallback: boolean) {
  if (typeof window === "undefined") {
    return fallback;
  }

  const storedPreference = window.localStorage.getItem(storageKey);
  if (storedPreference === "true") return true;
  if (storedPreference === "false") return false;
  return fallback;
}

export function SettingsPage() {
  const { auth, logout } = useAuth();
  const { setLanguageCode, t } = useI18n();
  const { themePreference, resolvedTheme, accentColor, setThemePreference, setAccentColor } = useTheme();
  const [activeSection, setActiveSection] = useState<SettingsSection>("appearance");
  const { profile, aiSettings, categories, importRules, exchangeRates, accounts = [], refresh } = useLedgerraData({
    accounts: true,
    profile: true,
    aiSettings: true,
    categories: true,
    importRules: true,
    exchangeRates: true
  });
  const [preferredCurrencyCode, setPreferredCurrencyCode] = useState("USD");
  const [preferredLanguageCode, setPreferredLanguageCode] = useState("en");
  const [defaultProvider, setDefaultProvider] = useState("OpenAi");
  const [openAiKey, setOpenAiKey] = useState("");
  const [anthropicKey, setAnthropicKey] = useState("");
  const [openAiCompatibleKey, setOpenAiCompatibleKey] = useState("");
  const [openAiCompatibleBaseUrl, setOpenAiCompatibleBaseUrl] = useState("");
  const [openAiCompatibleModel, setOpenAiCompatibleModel] = useState("");
  const [openAiCompatibleModels, setOpenAiCompatibleModels] = useState<string[]>([]);
  const [isLoadingOpenAiCompatibleModels, setIsLoadingOpenAiCompatibleModels] = useState(false);
  const [aiProviderError, setAiProviderError] = useState<string | null>(null);
  const [ruleName, setRuleName] = useState("");
  const [ruleAccountId, setRuleAccountId] = useState("");
  const [ruleAmountAbove, setRuleAmountAbove] = useState("");
  const [ruleAmountBelow, setRuleAmountBelow] = useState("");
  const [ruleMatchValue, setRuleMatchValue] = useState("");
  const [transactionType, setTransactionType] = useState("Expense");
  const [ruleCategoryId, setRuleCategoryId] = useState("");
  const [ruleError, setRuleError] = useState<string | null>(null);
  const [backupError, setBackupError] = useState<string | null>(null);
  const [backupNotice, setBackupNotice] = useState<string | null>(null);
  const [dangerError, setDangerError] = useState<string | null>(null);
  const [dangerNotice, setDangerNotice] = useState<string | null>(null);
  const [isDangerLoading, setIsDangerLoading] = useState(false);
  const [personalAccessTokens, setPersonalAccessTokens] = useState<PersonalAccessToken[]>([]);
  const [newTokenName, setNewTokenName] = useState("");
  const [createdToken, setCreatedToken] = useState<string | null>(null);
  const [tokenError, setTokenError] = useState<string | null>(null);
  const [densityPreference, setDensityPreference] = useState<DensityPreference>(() => resolveInitialDensityPreference());
  const [animationsEnabled, setAnimationsEnabled] = useState(() => resolveInitialBooleanPreference(animationStorageKey, true));
  const [minimalNavigation, setMinimalNavigation] = useState(() => resolveInitialBooleanPreference(minimalNavigationStorageKey, false));
  const [fxFromCurrencyCode, setFxFromCurrencyCode] = useState("EUR");
  const [fxMonth, setFxMonth] = useState("2026-01");
  const [fxRate, setFxRate] = useState("");
  const [fxError, setFxError] = useState<string | null>(null);

  const filteredCategories = useMemo(() => categories.filter((category) => category.kind === transactionType), [categories, transactionType]);

  useEffect(() => {
    setPreferredCurrencyCode(profile?.preferredCurrencyCode ?? "USD");
  }, [profile?.preferredCurrencyCode]);

  useEffect(() => {
    setPreferredLanguageCode(normalizeLanguageCode(profile?.preferredLanguageCode ?? "en").split("-")[0] ?? "en");
  }, [profile?.preferredLanguageCode]);

  const openAiProvider = aiSettings?.providers.openAi;
  const anthropicProvider = aiSettings?.providers.anthropic;
  const openAiCompatibleProvider = aiSettings?.providers.openAiCompatible;
  const openAiCompatibleBaseUrlSetting = openAiCompatibleProvider?.baseUrl ?? "";
  const openAiCompatibleModelSetting = openAiCompatibleProvider?.model ?? "";

  useEffect(() => {
    setDefaultProvider(aiSettings?.defaultProvider ?? "OpenAi");
  }, [aiSettings?.defaultProvider]);

  useEffect(() => {
    setOpenAiCompatibleBaseUrl(openAiCompatibleBaseUrlSetting);
    setOpenAiCompatibleModel(openAiCompatibleModelSetting);
  }, [openAiCompatibleBaseUrlSetting, openAiCompatibleModelSetting]);

  useEffect(() => {
    if (typeof document !== "undefined") {
      document.documentElement.dataset.density = densityPreference;
    }

    if (typeof window !== "undefined") {
      window.localStorage.setItem(densityStorageKey, densityPreference);
    }
  }, [densityPreference]);

  useEffect(() => {
    if (typeof document !== "undefined") {
      document.documentElement.dataset.motion = animationsEnabled ? "full" : "reduced";
      document.documentElement.dataset.navigationDensity = minimalNavigation ? "minimal" : "full";
    }

    if (typeof window !== "undefined") {
      window.localStorage.setItem(animationStorageKey, String(animationsEnabled));
      window.localStorage.setItem(minimalNavigationStorageKey, String(minimalNavigation));
    }
  }, [animationsEnabled, minimalNavigation]);

  useEffect(() => {
    if (filteredCategories.length === 0) {
      setRuleCategoryId("");
      return;
    }

    if (!filteredCategories.some((category) => category.id === ruleCategoryId)) {
      setRuleCategoryId(filteredCategories[0].id);
    }
  }, [filteredCategories, ruleCategoryId]);

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken) {
      return;
    }

    await apiClient.updateProfile(
      auth.accessToken,
      normalizeCurrencyCode(preferredCurrencyCode),
      normalizeLanguageCode(preferredLanguageCode)
    );
    setLanguageCode(preferredLanguageCode);
    await refresh();
  };


  const handleExchangeRateSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken) {
      return;
    }

    try {
      setFxError(null);
      const parsedRate = Number(fxRate);
      if (!Number.isFinite(parsedRate) || parsedRate <= 0) {
        setFxError("Enter a positive FX rate.");
        return;
      }

      await apiClient.upsertExchangeRate(auth.accessToken, {
        fromCurrencyCode: normalizeCurrencyCode(fxFromCurrencyCode),
        toCurrencyCode: normalizeCurrencyCode(preferredCurrencyCode),
        month: fxMonth,
        rate: parsedRate
      });
      setFxRate("");
      await refresh();
    } catch (exception) {
      setFxError(getErrorMessage(exception, "Unable to save FX rate."));
    }
  };

  const handleDeleteExchangeRate = async (rate: ExchangeRate) => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setFxError(null);
      await apiClient.deleteExchangeRate(auth.accessToken, rate.id);
      await refresh();
    } catch (exception) {
      setFxError(getErrorMessage(exception, "Unable to delete FX rate."));
    }
  };

  const handleAiProviderSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken) {
      return;
    }

    try {
      setAiProviderError(null);
      if (openAiKey.trim()) {
        await apiClient.saveAiProviderKey(auth.accessToken, "openai", openAiKey.trim());
      }

      if (anthropicKey.trim()) {
        await apiClient.saveAiProviderKey(auth.accessToken, "anthropic", anthropicKey.trim());
      }

      if (openAiCompatibleKey.trim()) {
        await apiClient.saveAiProviderKey(auth.accessToken, "openai-compatible", openAiCompatibleKey.trim(), {
          baseUrl: openAiCompatibleBaseUrl.trim(),
          model: openAiCompatibleModel.trim() || undefined
        });
      } else if (isOpenAiCompatibleConfigured && openAiCompatibleModel.trim() && openAiCompatibleModel.trim() !== openAiCompatibleProvider?.model) {
        await apiClient.updateAiProviderModel(auth.accessToken, "openai-compatible", openAiCompatibleModel.trim());
      }

      await apiClient.updateDefaultAiProvider(auth.accessToken, defaultProvider);
      await refresh();
    } catch (exception) {
      console.error(exception);
      setAiProviderError(exception instanceof Error ? exception.message : t("settings.unableToSaveAiSettings"));
      return;
    } finally {
      setOpenAiKey("");
      setAnthropicKey("");
      setOpenAiCompatibleKey("");
    }
  };

  const handleLoadOpenAiCompatibleModels = async () => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setAiProviderError(null);
      setIsLoadingOpenAiCompatibleModels(true);
      const response = await apiClient.getAiProviderModels(auth.accessToken, "openai-compatible");
      setOpenAiCompatibleModels(response.models);
      if (!openAiCompatibleModel && response.models.length > 0) {
        setOpenAiCompatibleModel(response.models[0]);
      }
    } catch (exception) {
      setAiProviderError(getErrorMessage(exception, t("settings.unableToLoadModels")));
    } finally {
      setIsLoadingOpenAiCompatibleModels(false);
    }
  };

  const handleRemoveProvider = async (provider: string) => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setAiProviderError(null);
      await apiClient.removeAiProviderKey(auth.accessToken, provider);
      await refresh();
    } catch (exception) {
      setAiProviderError(getErrorMessage(exception, t("settings.unableToRemoveProvider")));
    }
  };

  const handleRuleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken || !ruleCategoryId) {
      return;
    }

    try {
      setRuleError(null);
      const name = ruleName.trim();
      const value = ruleMatchValue.trim();
      if (!name || !value) {
        setRuleError(t("settings.ruleFieldsRequired"));
        return;
      }

      await apiClient.createImportRule(auth.accessToken, {
        name,
        matchField: "Note",
        matchOperator: "Contains",
        matchValue: value,
        assignCategoryId: ruleCategoryId,
        assignTransactionType: transactionType,
        priority: 100,
        isActive: true,
        ...((ruleAccountId || ruleAmountAbove || ruleAmountBelow) ? { conditions: [
          ...(ruleAccountId ? [{ field: "Account", operator: "Equals", value: ruleAccountId }] : []),
          ...(ruleAmountAbove ? [{ field: "Amount", operator: "GreaterThan", value: ruleAmountAbove }] : []),
          ...(ruleAmountBelow ? [{ field: "Amount", operator: "LessThan", value: ruleAmountBelow }] : [])
        ] } : {})
      });
      setRuleName("");
      setRuleMatchValue(""); setRuleAccountId(""); setRuleAmountAbove(""); setRuleAmountBelow("");
      await refresh();
    } catch (exception) {
      setRuleError(getErrorMessage(exception, t("settings.unableToSaveRule")));
    }
  };

  const handleToggleRule = async (rule: ImportRule) => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setRuleError(null);
      await apiClient.updateImportRule(auth.accessToken, { ...rule, isActive: !rule.isActive });
      await refresh();
    } catch (exception) {
      setRuleError(getErrorMessage(exception, t("settings.unableToUpdateRule")));
    }
  };

  const handleDeleteRule = async (ruleId: string) => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setRuleError(null);
      await apiClient.deleteImportRule(auth.accessToken, ruleId);
      await refresh();
    } catch (exception) {
      setRuleError(getErrorMessage(exception, t("settings.unableToDeleteRule")));
    }
  };

  const loadPersonalAccessTokens = useCallback(async () => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      const tokens = await apiClient.getPersonalAccessTokens(auth.accessToken);
      setPersonalAccessTokens(tokens.filter((token) => !token.revokedAtUtc));
    } catch (exception) {
      setTokenError(getErrorMessage(exception, t("settings.unableToLoadTokens")));
    }
  }, [auth?.accessToken, t]);

  const handleCreatePersonalAccessToken = async (event: FormEvent) => {
    event.preventDefault();
    if (!auth?.accessToken || !newTokenName.trim()) {
      return;
    }

    try {
      setTokenError(null);
      const response = await apiClient.createPersonalAccessToken(auth.accessToken, newTokenName.trim());
      setCreatedToken(response.plainTextToken);
      setNewTokenName("");
      await loadPersonalAccessTokens();
    } catch (exception) {
      setTokenError(getErrorMessage(exception, t("settings.unableToCreateToken")));
    }
  };

  const handleRevokePersonalAccessToken = async (id: string) => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setTokenError(null);
      await apiClient.revokePersonalAccessToken(auth.accessToken, id);
      setPersonalAccessTokens((tokens) => tokens.filter((token) => token.id !== id));
      await loadPersonalAccessTokens();
    } catch (exception) {
      setTokenError(getErrorMessage(exception, t("settings.unableToRevokeToken")));
    }
  };

  useEffect(() => {
    loadPersonalAccessTokens();
  }, [loadPersonalAccessTokens]);

  const isOpenAiConfigured = !!openAiProvider?.maskedKey;
  const isAnthropicConfigured = !!anthropicProvider?.maskedKey;
  const isOpenAiCompatibleConfigured = !!openAiCompatibleProvider?.maskedKey;
  const categoryNamesById = new Map(categories.map((category) => [category.id, category.name]));
  const configuredAiProviderCount = Number(isOpenAiConfigured) + Number(isAnthropicConfigured) + Number(isOpenAiCompatibleConfigured);
  const settingsGroups = [
    {
      label: t("settings.applicationGroup"),
      items: [
        { section: "appearance" as SettingsSection, label: t("settings.navAppearance"), icon: SettingsIcon },
        { section: "region" as SettingsSection, label: t("settings.navRegion"), icon: CashFlowIcon }
      ]
    },
    {
      label: t("settings.dataGroup"),
      items: [
        { section: "ai" as SettingsSection, label: t("settings.navAi"), icon: ReportsIcon, badge: configuredAiProviderCount },
        { section: "rules" as SettingsSection, label: t("settings.navRules"), icon: CategoryIcon, badge: importRules.length },
        { section: "backup" as SettingsSection, label: t("settings.navBackup"), icon: ImportsIcon }
      ]
    },
    {
      label: t("settings.accountGroup"),
      items: [
        { section: "profile" as SettingsSection, label: t("settings.profile"), icon: AccountsIcon },
        { section: "security" as SettingsSection, label: t("settings.security"), icon: SettingsIcon },
        { section: "danger" as SettingsSection, label: t("settings.dangerZone"), icon: SettingsIcon }
      ]
    }
  ];

  const sectionBreadcrumbs: Record<SettingsSection, string> = {
    appearance: t("settings.appearance"),
    region: t("settings.regionalPreferences"),
    profile: t("settings.profile"),
    security: t("settings.security"),
    ai: t("settings.aiProviders"),
    rules: t("settings.importRules"),
    backup: t("settings.backupAndRestore"),
    danger: t("settings.dangerZone")
  };
  const sectionDescriptions: Record<SettingsSection, string> = {
    appearance: t("settings.appearanceDescription"),
    region: t("settings.regionalPreferencesDescription"),
    profile: t("settings.profileDescription"),
    security: t("settings.securityDescription"),
    ai: t("settings.aiProvidersDescription"),
    rules: t("settings.importRulesDescription"),
    backup: t("settings.backupAndRestoreDescription"),
    danger: t("settings.dangerZoneDescription")
  };

  const accentLabels: Record<AccentColor, string> = {
    teal: t("settings.accentTeal"),
    blue: t("settings.accentBlue"),
    gold: t("settings.accentGold"),
    purple: t("settings.accentPurple"),
    coral: t("settings.accentCoral")
  };
  const themeOptions = [
    { value: "light" as ThemePreference, label: t("settings.themeLight"), previewClassName: "is-light" },
    { value: "dark" as ThemePreference, label: t("settings.themeDark"), previewClassName: "is-dark" },
    { value: "system" as ThemePreference, label: t("settings.themeSystem"), previewClassName: "is-system" }
  ];
  const densityOptions = [
    { value: "comfortable" as DensityPreference, label: t("settings.densityComfortable"), detail: "52 px" },
    { value: "standard" as DensityPreference, label: t("settings.densityStandard"), detail: "44 px" },
    { value: "compact" as DensityPreference, label: t("settings.densityCompact"), detail: "36 px" }
  ];
  const renderSectionIntro = (section: SettingsSection) => (
    <div className="settings-section-intro">
      <h1 id={`settings-${section}-title`}>{sectionBreadcrumbs[section]}</h1>
      <p>{sectionDescriptions[section]}</p>
    </div>
  );

  const handleExportBackup = async () => {
    if (!auth?.accessToken) {
      return;
    }

    try {
      setBackupError(null);
      const archive = await apiClient.exportBackup(auth.accessToken);
      const blob = new Blob([JSON.stringify(archive, null, 2)], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = `ledgerra-backup-${new Date().toISOString().slice(0, 10)}.json`;
      link.click();
      URL.revokeObjectURL(url);
      setBackupNotice(t("settings.backupExported"));
    } catch (exception) {
      setBackupError(getErrorMessage(exception, t("settings.unableToExportBackup")));
    }
  };

  const handleRestoreBackup = async (event: ChangeEvent<HTMLInputElement>) => {
    if (!auth?.accessToken) {
      return;
    }

    const file = event.currentTarget.files?.[0];
    if (!file) {
      return;
    }

    try {
      setBackupError(null);
      const content = await file.text();
      const archive = JSON.parse(content);

      const confirmed = window.confirm(t("settings.restoreBackupConfirm"));

      if (!confirmed) {
        return;
      }

      await apiClient.restoreBackup(auth.accessToken, archive);
      await refresh();
      setBackupNotice(t("settings.backupRestored"));
    } catch (exception) {
      setBackupError(getErrorMessage(exception, t("settings.unableToRestoreBackup")));
    } finally {
      event.currentTarget.value = "";
    }
  };

  const handleClearAccountData = async () => {
    if (!auth?.accessToken || isDangerLoading) {
      return;
    }

    const confirmed = window.confirm(t("settings.clearAccountDataConfirm"));
    if (!confirmed) {
      return;
    }

    try {
      setIsDangerLoading(true);
      setDangerError(null);
      setDangerNotice(null);
      await apiClient.clearAccountData(auth.accessToken);
      await refresh();
      setDangerNotice(t("settings.accountDataCleared"));
    } catch (exception) {
      setDangerError(getErrorMessage(exception, t("settings.unableToClearAccountData")));
    } finally {
      setIsDangerLoading(false);
    }
  };

  const handleDeleteAccount = async () => {
    if (!auth?.accessToken || isDangerLoading) {
      return;
    }

    const confirmed = window.confirm(t("settings.deleteAccountConfirm"));
    if (!confirmed) {
      return;
    }

    try {
      setIsDangerLoading(true);
      setDangerError(null);
      setDangerNotice(null);
      await apiClient.deleteAccount(auth.accessToken);
      logout();
    } catch (exception) {
      setDangerError(getErrorMessage(exception, t("settings.unableToDeleteAccount")));
    } finally {
      setIsDangerLoading(false);
    }
  };

  return (
    <div className="settings-page">
      <header className="settings-hero">
        <div>
          <div className="settings-breadcrumb">
            <span>Ledgerra</span>
            <span>/</span>
            <span>{t("settings.eyebrow")}</span>
            <span>/</span>
            <strong>{sectionBreadcrumbs[activeSection]}</strong>
          </div>
        </div>
        <div className="settings-search" aria-hidden="true">
          <span>⌕</span>
          <span>{t("settings.searchPlaceholder")}</span>
          <kbd>⌘K</kbd>
        </div>
      </header>

      <div className="settings-workspace-grid">
        <aside className="settings-subnav" aria-label={t("settings.sections")}>
          {settingsGroups.map((group) => (
            <div className="settings-subnav-group" key={group.label}>
              <span>{group.label}</span>
              {group.items.map((item) => (
                <button
                  type="button"
                  className={`settings-subnav-link${activeSection === item.section ? " active" : ""}`}
                  key={`${group.label}-${item.label}`}
                  onClick={() => setActiveSection(item.section)}
                >
                  <item.icon />
                  <span>{item.label}</span>
                  {typeof item.badge === "number" ? <strong>{item.badge}</strong> : null}
                </button>
              ))}
            </div>
          ))}
        </aside>

        <div className="settings-main-panels">
          {activeSection === "appearance" && (
            <section className="settings-section-stack" aria-labelledby="settings-appearance-title">
              {renderSectionIntro("appearance")}

              <div className="settings-appearance-grid">
                <article className="settings-preference-panel">
                  <div className="settings-panel-heading">
                    <span className="settings-panel-icon">
                      <SettingsIcon />
                    </span>
                    <div>
                      <h2>{t("settings.theme")}</h2>
                      <p>
                        {t("settings.themeDescription", {
                          theme: resolvedTheme === "dark" ? t("settings.themeDark").toLowerCase() : t("settings.themeLight").toLowerCase()
                        })}
                      </p>
                    </div>
                  </div>
                  <div className="settings-theme-options" aria-label={t("settings.theme")}>
                    {themeOptions.map((option) => (
                      <button
                        key={option.value}
                        type="button"
                        className={`settings-theme-option${themePreference === option.value ? " active" : ""}`}
                        aria-pressed={themePreference === option.value}
                        onClick={() => setThemePreference(option.value)}
                      >
                        <span className={`settings-theme-preview ${option.previewClassName}`}>
                          <span />
                        </span>
                        <strong>{option.label}</strong>
                      </button>
                    ))}
                  </div>
                </article>

                <article className="settings-preference-panel">
                  <div className="settings-panel-heading">
                    <span className="settings-panel-icon settings-panel-icon--accent" />
                    <div>
                      <h2>{t("settings.accent")}</h2>
                      <p>{t("settings.accentDescription")}</p>
                    </div>
                  </div>
                  <div className="settings-accent-options" aria-label={t("settings.accent")}>
                    {accentPresets.map((preset) => (
                      <button
                        key={preset.id}
                        type="button"
                        className={`settings-accent-chip${accentColor === preset.id ? " active" : ""}`}
                        aria-pressed={accentColor === preset.id}
                        onClick={() => setAccentColor(preset.id)}
                      >
                        <span style={{ background: preset.swatch }} />
                        {accentLabels[preset.id]}
                      </button>
                    ))}
                  </div>
                </article>
              </div>

              <article className="settings-preference-panel">
                <div className="settings-panel-heading">
                  <span className="settings-panel-icon settings-panel-icon--text">Tt</span>
                  <div>
                    <h2>{t("settings.density")}</h2>
                    <p>{t("settings.densityDescription")}</p>
                  </div>
                </div>
                <div className="settings-density-options" aria-label={t("settings.density")}>
                  {densityOptions.map((option) => (
                    <button
                      key={option.value}
                      type="button"
                      className={`settings-density-option${densityPreference === option.value ? " active" : ""}`}
                      aria-pressed={densityPreference === option.value}
                      onClick={() => setDensityPreference(option.value)}
                    >
                      <span className={`settings-density-preview is-${option.value}`}>
                        <span />
                        <span />
                        <span />
                      </span>
                      <strong>{option.label}</strong>
                      <em>{option.detail}</em>
                    </button>
                  ))}
                </div>
              </article>

              <article className="settings-preference-panel settings-motion-panel">
                <div className="settings-panel-heading">
                  <span className="settings-panel-icon settings-panel-icon--motion" />
                  <div>
                    <h2>{t("settings.motionAndChrome")}</h2>
                  </div>
                </div>
                <div className="settings-toggle-list">
                  <label className="settings-toggle-row">
                    <span>
                      <strong>{t("settings.transitionAnimations")}</strong>
                      <small>{t("settings.transitionAnimationsDescription")}</small>
                    </span>
                    <input
                      type="checkbox"
                      checked={animationsEnabled}
                      onChange={(event) => setAnimationsEnabled(event.target.checked)}
                    />
                  </label>
                  <label className="settings-toggle-row">
                    <span>
                      <strong>{t("settings.minimalNavigation")}</strong>
                      <small>{t("settings.minimalNavigationDescription")}</small>
                    </span>
                    <input
                      type="checkbox"
                      checked={minimalNavigation}
                      onChange={(event) => setMinimalNavigation(event.target.checked)}
                    />
                  </label>
                </div>
                <p className="settings-save-status">{t("settings.everythingSaved")}</p>
              </article>
            </section>
          )}

          {activeSection === "region" && (
            <section className="settings-section-stack" aria-labelledby="settings-region-title">
              {renderSectionIntro("region")}
              <div className="settings-content-card">
                <SectionCard title={t("settings.regionalPreferences")} icon={<CashFlowIcon />} hideHeader>
                  <form className="settings-form-grid" onSubmit={handleSubmit}>
                    <label>
                      {t("settings.preferredCurrency")}
                      <select value={preferredCurrencyCode} onChange={(event) => setPreferredCurrencyCode(event.target.value)}>
                        {supportedCurrencies.map((currency) => (
                          <option key={currency.code} value={currency.code}>
                            {currency.label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label>
                      {t("settings.preferredLanguage")}
                      <select value={preferredLanguageCode} onChange={(event) => setPreferredLanguageCode(event.target.value)}>
                        {supportedLanguages.map((language) => (
                          <option key={language.code} value={language.code}>
                            {language.label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <button className="primary-button" type="submit">
                      {t("settings.savePreferences")}
                    </button>
                  </form>
                </SectionCard>
              </div>

              <div className="settings-content-card">
                <SectionCard title="Manual FX rates" icon={<CashFlowIcon />}>
                  <form className="settings-form-grid" onSubmit={handleExchangeRateSubmit}>
                    {fxError ? <p className="error-banner">{fxError}</p> : null}
                    <label>
                      From currency
                      <select value={fxFromCurrencyCode} onChange={(event) => setFxFromCurrencyCode(event.target.value)}>
                        {supportedCurrencies.map((currency) => (
                          <option key={currency.code} value={currency.code}>
                            {currency.label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label>
                      To profile currency
                      <input value={normalizeCurrencyCode(preferredCurrencyCode)} readOnly />
                    </label>
                    <label>
                      Month
                      <input type="month" value={fxMonth} onChange={(event) => setFxMonth(event.target.value)} />
                    </label>
                    <label>
                      Rate
                      <input inputMode="decimal" value={fxRate} onChange={(event) => setFxRate(event.target.value)} placeholder="1.0000" />
                    </label>
                    <button className="primary-button" type="submit">Save FX rate</button>
                  </form>

                  <div className="settings-provider-list" aria-label="Manual FX rates">
                    {exchangeRates.length === 0 ? (
                      <p className="field-hint">Add monthly rates for accounts that differ from your profile currency.</p>
                    ) : exchangeRates.map((rate) => (
                      <article className="settings-provider-row" key={rate.id}>
                        <div>
                          <strong>{rate.fromCurrencyCode} → {rate.toCurrencyCode}</strong>
                          <p>{rate.month}</p>
                        </div>
                        <div className="settings-provider-actions">
                          <strong>{rate.rate}</strong>
                          <ActionMenu label={t("settings.manualFxRateActions", { from: rate.fromCurrencyCode, to: rate.toCurrencyCode, month: rate.month })}>
                            <button
                              className="action-menu-item danger-button"
                              type="button"
                              aria-label={t("settings.deleteFxRate", { from: rate.fromCurrencyCode, to: rate.toCurrencyCode, month: rate.month })}
                              onClick={() => void handleDeleteExchangeRate(rate)}
                            >
                              {t("common.delete")}
                            </button>
                          </ActionMenu>
                        </div>
                      </article>
                    ))}
                  </div>
                </SectionCard>
              </div>
            </section>
          )}

          {activeSection === "ai" && (
            <section className="settings-section-stack" aria-labelledby="settings-ai-title">
              {renderSectionIntro("ai")}
              <div className="settings-content-card">
                <SectionCard title={t("settings.aiProviders")} icon={<ReportsIcon />} hideHeader>
                  <div className="settings-ai-layout">
                    <form className="stack-form settings-ai-form" onSubmit={handleAiProviderSubmit}>
                      {aiProviderError ? <p className="error-banner">{aiProviderError}</p> : null}
                      <label>
                        {t("settings.defaultProvider")}
                        <select value={defaultProvider} onChange={(event) => setDefaultProvider(event.target.value)}>
                          <option value="OpenAi">OpenAI</option>
                          <option value="Anthropic">Anthropic</option>
                          <option value="OpenAiCompatible">{t("settings.openAiCompatibleProvider")}</option>
                        </select>
                      </label>
                      <div className="settings-key-grid">
                        <label>
                          {t("settings.openAiApiKey")}
                          <input
                            value={openAiKey}
                            onChange={(event) => setOpenAiKey(event.target.value)}
                            type="password"
                            placeholder={openAiProvider?.maskedKey ?? t("common.notConfigured")}
                          />
                        </label>
                        <label>
                          {t("settings.anthropicApiKey")}
                          <input
                            value={anthropicKey}
                            onChange={(event) => setAnthropicKey(event.target.value)}
                            type="password"
                            placeholder={anthropicProvider?.maskedKey ?? t("common.notConfigured")}
                          />
                        </label>
                      </div>
                      <div className="settings-compatible-provider-panel">
                        <div>
                          <h3>{t("settings.openAiCompatibleProvider")}</h3>
                          <p>{t("settings.openAiCompatibleDescription")}</p>
                        </div>
                        <div className="settings-key-grid">
                          <label>
                            {t("settings.openAiCompatibleApiKey")}
                            <input
                              value={openAiCompatibleKey}
                              onChange={(event) => setOpenAiCompatibleKey(event.target.value)}
                              type="password"
                              placeholder={openAiCompatibleProvider?.maskedKey ?? t("common.notConfigured")}
                            />
                          </label>
                          <label>
                            {t("settings.openAiCompatibleBaseUrl")}
                            <input
                              value={openAiCompatibleBaseUrl}
                              onChange={(event) => setOpenAiCompatibleBaseUrl(event.target.value)}
                              placeholder="https://api.provider.example/v1"
                            />
                          </label>
                        </div>
                        <div className="settings-model-row">
                          <label>
                            {t("settings.openAiCompatibleModel")}
                            {openAiCompatibleModels.length > 0 ? (
                              <select value={openAiCompatibleModel} onChange={(event) => setOpenAiCompatibleModel(event.target.value)}>
                                <option value="">{t("settings.chooseModel")}</option>
                                {openAiCompatibleModels.map((model) => (
                                  <option key={model} value={model}>{model}</option>
                                ))}
                              </select>
                            ) : (
                              <input
                                value={openAiCompatibleModel}
                                onChange={(event) => setOpenAiCompatibleModel(event.target.value)}
                                placeholder="model-id"
                              />
                            )}
                          </label>
                          <button
                            className="ghost-button compact-button"
                            type="button"
                            disabled={!isOpenAiCompatibleConfigured || isLoadingOpenAiCompatibleModels}
                            onClick={() => void handleLoadOpenAiCompatibleModels()}
                          >
                            {isLoadingOpenAiCompatibleModels ? t("settings.loadingModels") : t("settings.loadModels")}
                          </button>
                        </div>
                        {!isOpenAiCompatibleConfigured ? (
                          <p className="field-hint">{t("settings.configureCompatibleProviderFirst")}</p>
                        ) : null}
                      </div>
                      <button className="primary-button" type="submit">
                        {t("settings.saveAiSettings")}
                      </button>
                    </form>

                    <div className="settings-provider-list">
                      <article className="settings-provider-row">
                        <div>
                          <strong>OpenAI</strong>
                          <p>{openAiProvider?.isConfigured ? t("common.configured") : t("common.notConfigured")}</p>
                        </div>
                        <div className="settings-provider-actions">
                          <strong>{openAiProvider?.maskedKey ?? t("common.notConfigured")}</strong>
                          <ActionMenu label={t("settings.providerActions", { provider: "OpenAI" })}>
                            <button
                              className="action-menu-item"
                              type="button"
                              aria-label={t("settings.removeProvider", { provider: "OpenAI" })}
                              disabled={!isOpenAiConfigured}
                              onClick={() => {
                                if (isOpenAiConfigured) {
                                  void handleRemoveProvider("openai");
                                }
                              }}
                            >
                              {t("common.remove")}
                            </button>
                          </ActionMenu>
                        </div>
                      </article>
                      <article className="settings-provider-row">
                        <div>
                          <strong>Anthropic</strong>
                          <p>{anthropicProvider?.isConfigured ? t("common.configured") : t("common.notConfigured")}</p>
                        </div>
                        <div className="settings-provider-actions">
                          <strong>{anthropicProvider?.maskedKey ?? t("common.notConfigured")}</strong>
                          <ActionMenu label={t("settings.providerActions", { provider: "Anthropic" })}>
                            <button
                              className="action-menu-item"
                              type="button"
                              aria-label={t("settings.removeProvider", { provider: "Anthropic" })}
                              disabled={!isAnthropicConfigured}
                              onClick={() => {
                                if (isAnthropicConfigured) {
                                  void handleRemoveProvider("anthropic");
                                }
                              }}
                            >
                              {t("common.remove")}
                            </button>
                          </ActionMenu>
                        </div>
                      </article>
                      <article className="settings-provider-row">
                        <div>
                          <strong>{t("settings.openAiCompatibleProvider")}</strong>
                          <p>
                            {openAiCompatibleProvider?.isConfigured ? t("common.configured") : t("common.notConfigured")}
                            {openAiCompatibleProvider?.baseUrl ? ` · ${openAiCompatibleProvider.baseUrl}` : ""}
                            {openAiCompatibleProvider?.model ? ` · ${openAiCompatibleProvider.model}` : ""}
                          </p>
                        </div>
                        <div className="settings-provider-actions">
                          <strong>{openAiCompatibleProvider?.maskedKey ?? t("common.notConfigured")}</strong>
                          <ActionMenu label={t("settings.providerActions", { provider: t("settings.openAiCompatibleProvider") })}>
                            <button
                              className="action-menu-item"
                              type="button"
                              aria-label={t("settings.removeProvider", { provider: t("settings.openAiCompatibleProvider") })}
                              disabled={!isOpenAiCompatibleConfigured}
                              onClick={() => {
                                if (isOpenAiCompatibleConfigured) {
                                  void handleRemoveProvider("openai-compatible");
                                }
                              }}
                            >
                              {t("common.remove")}
                            </button>
                          </ActionMenu>
                        </div>
                      </article>
                    </div>
                  </div>
                </SectionCard>
              </div>
            </section>
          )}

          {activeSection === "rules" && (
            <section className="settings-section-stack" aria-labelledby="settings-rules-title">
              {renderSectionIntro("rules")}
              <div className="settings-content-card">
                <SectionCard title={t("settings.importRules")} icon={<CategoryIcon />} hideHeader>
                  <form className="stack-form rule-form" onSubmit={handleRuleSubmit}>
                    {ruleError ? <p className="error-banner">{ruleError}</p> : null}
                    <label>
                      {t("settings.ruleName")}
                      <input value={ruleName} onChange={(event) => setRuleName(event.target.value)} placeholder={t("settings.ruleNamePlaceholder")} required />
                    </label>
                    <label>
                      {t("settings.matchText")}
                      <input value={ruleMatchValue} onChange={(event) => setRuleMatchValue(event.target.value)} placeholder={t("settings.matchTextPlaceholder")} required />
                    </label>
                    <label>
                      {t("settings.transactionType")}
                      <select value={transactionType} onChange={(event) => setTransactionType(event.target.value)}>
                        <option value="Expense">{t("transactionType.Expense")}</option>
                        <option value="Income">{t("transactionType.Income")}</option>
                      </select>
                    </label>
                    <label>
                      {t("settings.category")}
                      <select value={ruleCategoryId} onChange={(event) => setRuleCategoryId(event.target.value)} disabled={filteredCategories.length === 0}>
                        {filteredCategories.map((category) => (
                          <option key={category.id} value={category.id}>
                            {category.name}
                          </option>
                        ))}
                      </select>
                    </label>
                    <details><summary>Additional rule conditions (all must match)</summary>
                      <label>Only in account<select value={ruleAccountId} onChange={event => setRuleAccountId(event.target.value)}><option value="">Any account</option>{accounts.map(account => <option key={account.id} value={account.id}>{account.name}</option>)}</select></label>
                      <label>Amount greater than<input type="number" min="0" step="0.01" value={ruleAmountAbove} onChange={event => setRuleAmountAbove(event.target.value)} /></label>
                      <label>Amount less than<input type="number" min="0" step="0.01" value={ruleAmountBelow} onChange={event => setRuleAmountBelow(event.target.value)} /></label>
                    </details>
                    <button className="primary-button" type="submit" disabled={filteredCategories.length === 0}>
                      {t("settings.addRule")}
                    </button>
                  </form>
                  <div className="table-list compact-list rule-list">
                    {importRules.length === 0 ? (
                      <p className="empty-state">{t("settings.noRules")}</p>
                    ) : (
                      importRules.map((rule) => (
                        <article className="table-row rule-row" key={rule.id}>
                          <div>
                            <strong>{rule.name}</strong>
                            <p>
                              {rule.matchField} {rule.matchOperator.toLowerCase()} "{rule.matchValue}" {rule.conditions?.map(condition => `AND ${condition.field} ${condition.operator} ${condition.value}`).join(" ")} -&gt;{" "}
                              {categoryNamesById.get(rule.assignCategoryId) ?? t("settings.unknownCategory")}
                            </p>
                          </div>
                          <div className="rule-actions">
                            <strong>{rule.isActive ? t("common.active") : t("common.disabled")}</strong>
                            <ActionMenu label={`Rule actions for ${rule.name}`}>
                              <button className="action-menu-item" type="button" aria-label={`${rule.isActive ? t("common.disable") : t("common.enable")} ${rule.name}`} onClick={() => void handleToggleRule(rule)}>
                                {rule.isActive ? t("common.disable") : t("common.enable")}
                              </button>
                              <button className="action-menu-item danger-button" type="button" aria-label={`${t("common.delete")} ${rule.name}`} onClick={() => void handleDeleteRule(rule.id)}>
                                {t("common.delete")}
                              </button>
                            </ActionMenu>
                          </div>
                        </article>
                      ))
                    )}
                  </div>
                </SectionCard>
              </div>
            </section>
          )}

          {activeSection === "profile" && (
            <section className="settings-section-stack" aria-labelledby="settings-profile-title">
              {renderSectionIntro("profile")}
              <div className="settings-content-card">
                <SectionCard title={t("settings.profile")} icon={<AccountsIcon />} hideHeader>
                  <div className="settings-session-grid">
                    <article className="settings-fact">
                      <span>{t("settings.userEmail")}</span>
                      <strong>{profile?.email ?? auth?.email ?? t("common.unknown")}</strong>
                      <p>{t("settings.activeLocalAccount")}</p>
                    </article>
                    <article className="settings-fact">
                      <span>{t("settings.mainCurrency")}</span>
                      <strong>{profile?.preferredCurrencyCode ?? "USD"}</strong>
                      <p>{t("settings.appWideTotals")}</p>
                    </article>
                    <article className="settings-fact">
                      <span>{t("settings.preferredLanguage")}</span>
                      <strong>{supportedLanguages.find((language) => language.code === (profile?.preferredLanguageCode ?? preferredLanguageCode))?.label ?? preferredLanguageCode.toUpperCase()}</strong>
                      <p>{t("settings.languageAndFormatting")}</p>
                    </article>
                  </div>
                </SectionCard>
              </div>
            </section>
          )}

          {activeSection === "security" && (
            <section className="settings-section-stack" aria-labelledby="settings-security-title">
              {renderSectionIntro("security")}
              <div className="settings-content-card">
                <SectionCard title={t("settings.security")} icon={<SettingsIcon />} hideHeader>
                  <div className="settings-session-grid">
                    <article className="settings-fact">
                      <span>{t("settings.apiModel")}</span>
                      <strong>{t("settings.v1Ready")}</strong>
                      <p>{t("settings.singleUserJwt")}</p>
                    </article>
                    <article className="settings-fact">
                      <span>{t("settings.mobileReadiness")}</span>
                      <strong>{t("settings.prepared")}</strong>
                      <p>{t("settings.mobileReadinessDescription")}</p>
                    </article>
                  </div>

                  <div className="settings-token-section">
                    <h3>{t("settings.personalAccessTokens")}</h3>
                    <p className="helper-text">{t("settings.personalAccessTokensDescription")}</p>
                    {tokenError ? <p className="error-banner">{tokenError}</p> : null}
                    {createdToken ? (
                      <div className="success-banner settings-inline-banner">
                        <p>{t("settings.tokenCreatedNotice")}</p>
                        <code className="settings-token-value">{createdToken}</code>
                        <button className="ghost-button compact-button" type="button" onClick={() => setCreatedToken(null)}>
                          {t("common.dismiss")}
                        </button>
                      </div>
                    ) : null}
                    <form className="settings-token-form" onSubmit={(event) => void handleCreatePersonalAccessToken(event)}>
                      <input
                        value={newTokenName}
                        onChange={(event) => setNewTokenName(event.target.value)}
                        placeholder={t("settings.tokenNamePlaceholder")}
                        required
                      />
                      <button className="primary-button compact-button" type="submit">
                        {t("settings.createToken")}
                      </button>
                    </form>
                    <div className="table-list compact-list">
                      {personalAccessTokens.length === 0 ? (
                        <p className="empty-state">{t("settings.noTokens")}</p>
                      ) : (
                        personalAccessTokens.map((pat) => (
                          <article className="table-row" key={pat.id}>
                            <div>
                              <strong>{pat.name}</strong>
                              <p>{pat.tokenPrefix}...</p>
                            </div>
                            <ActionMenu label={`Token actions for ${pat.name}`}>
                              <button
                                className="action-menu-item danger-button"
                                type="button"
                                aria-label={`${t("settings.revokeToken")} ${pat.name}`}
                                onClick={() => void handleRevokePersonalAccessToken(pat.id)}
                              >
                                {t("settings.revokeToken")}
                              </button>
                            </ActionMenu>
                          </article>
                        ))
                      )}
                    </div>
                  </div>
                </SectionCard>
              </div>
            </section>
          )}

          {activeSection === "backup" && (
            <section className="settings-section-stack" aria-labelledby="settings-backup-title">
              {renderSectionIntro("backup")}
              <div className="settings-content-card">
                <SectionCard title={t("settings.backupAndRestore")} icon={<ImportsIcon />} hideHeader>
                  <div className="stack-form settings-backup-panel">
                    {backupError ? <p className="error-banner">{backupError}</p> : null}
                    {backupNotice ? <p className="success-banner settings-inline-banner">{backupNotice}</p> : null}
                    <button className="primary-button" type="button" onClick={() => void handleExportBackup()}>
                      {t("settings.exportBackup")}
                    </button>
                    <label>
                      {t("settings.restoreBackup")}
                      <input type="file" accept="application/json" onChange={(event) => void handleRestoreBackup(event)} />
                    </label>
                  </div>
                </SectionCard>
              </div>
            </section>
          )}

          {activeSection === "danger" && (
            <section className="settings-section-stack" aria-labelledby="settings-danger-title">
              {renderSectionIntro("danger")}
              <div className="settings-content-card">
                <SectionCard title={t("settings.dangerZone")} icon={<SettingsIcon />} hideHeader>
                  <div className="settings-danger-panel">
                    {dangerError ? <p className="error-banner">{dangerError}</p> : null}
                    {dangerNotice ? <p className="success-banner settings-inline-banner">{dangerNotice}</p> : null}
                    <article className="settings-danger-action">
                      <div>
                        <h2>{t("settings.clearAccountData")}</h2>
                        <p>{t("settings.clearAccountDataDescription")}</p>
                      </div>
                      <button className="ghost-button danger-button" type="button" disabled={isDangerLoading} onClick={() => void handleClearAccountData()}>
                        {t("settings.clearAllData")}
                      </button>
                    </article>
                    <article className="settings-danger-action">
                      <div>
                        <h2>{t("settings.deleteAccount")}</h2>
                        <p>{t("settings.deleteAccountDescription")}</p>
                      </div>
                      <button className="ghost-button danger-button" type="button" disabled={isDangerLoading} onClick={() => void handleDeleteAccount()}>
                        {t("settings.deleteAccount")}
                      </button>
                    </article>
                  </div>
                </SectionCard>
              </div>
            </section>
          )}
        </div>
      </div>
    </div>
  );
}
