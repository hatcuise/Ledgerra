import type { Dispatch, ReactNode, SetStateAction } from "react";
import { createContext, useContext, useEffect, useState } from "react";
import type { MonthlyReportAnalysis, MonthlyReportAnalysisJob, MonthlyReportDraftTransaction } from "../types";

type ImportAnalysisContextValue = {
  accountId: string;
  setAccountId: Dispatch<SetStateAction<string>>;
  month: string;
  setMonth: Dispatch<SetStateAction<string>>;
  provider: string;
  setProvider: Dispatch<SetStateAction<string>>;
  file: File | null;
  setFile: Dispatch<SetStateAction<File | null>>;
  csvHeaders: string[];
  setCsvHeaders: Dispatch<SetStateAction<string[]>>;
  dateColumn: string;
  setDateColumn: Dispatch<SetStateAction<string>>;
  invertAmounts: boolean;
  setInvertAmounts: Dispatch<SetStateAction<boolean>>;
  amountColumn: string;
  setAmountColumn: Dispatch<SetStateAction<string>>;
  descriptionColumn: string;
  setDescriptionColumn: Dispatch<SetStateAction<string>>;
  drafts: MonthlyReportDraftTransaction[];
  setDrafts: Dispatch<SetStateAction<MonthlyReportDraftTransaction[]>>;
  draftDateInputs: Record<string, string>;
  setDraftDateInputs: Dispatch<SetStateAction<Record<string, string>>>;
  selected: Set<string>;
  setSelected: Dispatch<SetStateAction<Set<string>>>;
  acceptedDuplicateSourceIds: Set<string>;
  setAcceptedDuplicateSourceIds: Dispatch<SetStateAction<Set<string>>>;
  error: string | null;
  setError: Dispatch<SetStateAction<string | null>>;
  ruleMessage: string | null;
  setRuleMessage: Dispatch<SetStateAction<string | null>>;
  isAnalyzing: boolean;
  setIsAnalyzing: Dispatch<SetStateAction<boolean>>;
  analysisElapsedSeconds: number;
  analysisJob: MonthlyReportAnalysisJob | null;
  setAnalysisJob: Dispatch<SetStateAction<MonthlyReportAnalysisJob | null>>;
  isCommitting: boolean;
  setIsCommitting: Dispatch<SetStateAction<boolean>>;
  rememberingRuleSourceId: string | null;
  setRememberingRuleSourceId: Dispatch<SetStateAction<string | null>>;
  isRememberingSelectedRules: boolean;
  setIsRememberingSelectedRules: Dispatch<SetStateAction<boolean>>;
  hideDuplicates: boolean;
  setHideDuplicates: Dispatch<SetStateAction<boolean>>;
  bulkCategoryId: string;
  setBulkCategoryId: Dispatch<SetStateAction<string>>;
  applyAnalysis: (analysis: MonthlyReportAnalysis) => void;
  clearReviewSession: () => void;
};

const ImportAnalysisContext = createContext<ImportAnalysisContextValue | undefined>(undefined);

function formatDraftDate(value: string) {
  if (!value) {
    return "";
  }

  const isoDate = value.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (isoDate) {
    return `${isoDate[3]}-${isoDate[2]}-${isoDate[1]}`;
  }

  const parsedDate = new Date(value);
  if (Number.isNaN(parsedDate.getTime())) {
    return value;
  }

  const day = String(parsedDate.getUTCDate()).padStart(2, "0");
  const month = String(parsedDate.getUTCMonth() + 1).padStart(2, "0");
  const year = parsedDate.getUTCFullYear();
  return `${day}-${month}-${year}`;
}

export function ImportAnalysisProvider({ children }: { children: ReactNode }) {
  const [accountId, setAccountId] = useState("");
  const [month, setMonth] = useState(new Date().toISOString().slice(0, 7));
  const [provider, setProvider] = useState("OpenAi");
  const [file, setFile] = useState<File | null>(null);
  const [csvHeaders, setCsvHeaders] = useState<string[]>([]);
  const [dateColumn, setDateColumn] = useState("");
  const [invertAmounts, setInvertAmounts] = useState(false);
  const [amountColumn, setAmountColumn] = useState("");
  const [descriptionColumn, setDescriptionColumn] = useState("");
  const [drafts, setDrafts] = useState<MonthlyReportDraftTransaction[]>([]);
  const [draftDateInputs, setDraftDateInputs] = useState<Record<string, string>>({});
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [acceptedDuplicateSourceIds, setAcceptedDuplicateSourceIds] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [ruleMessage, setRuleMessage] = useState<string | null>(null);
  const [isAnalyzing, setIsAnalyzing] = useState(false);
  const [analysisElapsedSeconds, setAnalysisElapsedSeconds] = useState(0);
  const [analysisJob, setAnalysisJob] = useState<MonthlyReportAnalysisJob | null>(null);
  const [isCommitting, setIsCommitting] = useState(false);
  const [rememberingRuleSourceId, setRememberingRuleSourceId] = useState<string | null>(null);
  const [isRememberingSelectedRules, setIsRememberingSelectedRules] = useState(false);
  const [hideDuplicates, setHideDuplicates] = useState(false);
  const [bulkCategoryId, setBulkCategoryId] = useState("");

  useEffect(() => {
    if (!isAnalyzing) {
      setAnalysisElapsedSeconds(0);
      return;
    }

    setAnalysisElapsedSeconds(0);
    const timerId = window.setInterval(() => {
      setAnalysisElapsedSeconds((current) => current + 1);
    }, 1000);

    return () => window.clearInterval(timerId);
  }, [isAnalyzing]);

  const applyAnalysis = (analysis: MonthlyReportAnalysis) => {
    setDrafts(analysis.transactions);
    setDraftDateInputs(
      Object.fromEntries(
        analysis.transactions.map((transaction) => [transaction.sourceId, formatDraftDate(transaction.occurredOnUtc)])
      )
    );
    setSelected(
      new Set(
        analysis.transactions
          .filter((transaction) => transaction.isSelectedByDefault ?? !transaction.isLikelyDuplicate)
          .map((transaction) => transaction.sourceId)
      )
    );
    setAcceptedDuplicateSourceIds(
      new Set(
        analysis.transactions
          .filter((transaction) => transaction.isLikelyDuplicate && transaction.isSelectedByDefault)
          .map((transaction) => transaction.sourceId)
      )
    );
    setHideDuplicates(false);
    setBulkCategoryId("");
  };

  const clearReviewSession = () => {
    setDrafts([]);
    setDraftDateInputs({});
    setSelected(new Set());
    setAcceptedDuplicateSourceIds(new Set());
    setAnalysisJob(null);
    setHideDuplicates(false);
    setBulkCategoryId("");
  };

  return (
    <ImportAnalysisContext.Provider
      value={{
        accountId,
        setAccountId,
        month,
        setMonth,
        provider,
        setProvider,
        file,
        setFile,
        csvHeaders,
        setCsvHeaders,
        dateColumn,
        setDateColumn,
        invertAmounts,
        setInvertAmounts,
        amountColumn,
        setAmountColumn,
        descriptionColumn,
        setDescriptionColumn,
        drafts,
        setDrafts,
        draftDateInputs,
        setDraftDateInputs,
        selected,
        setSelected,
        acceptedDuplicateSourceIds,
        setAcceptedDuplicateSourceIds,
        error,
        setError,
        ruleMessage,
        setRuleMessage,
        isAnalyzing,
        setIsAnalyzing,
        analysisElapsedSeconds,
        analysisJob,
        setAnalysisJob,
        isCommitting,
        setIsCommitting,
        rememberingRuleSourceId,
        setRememberingRuleSourceId,
        isRememberingSelectedRules,
        setIsRememberingSelectedRules,
        hideDuplicates,
        setHideDuplicates,
        bulkCategoryId,
        setBulkCategoryId,
        applyAnalysis,
        clearReviewSession
      }}
    >
      {children}
    </ImportAnalysisContext.Provider>
  );
}

export function useImportAnalysis() {
  const context = useContext(ImportAnalysisContext);
  if (!context) {
    throw new Error("useImportAnalysis must be used inside ImportAnalysisProvider");
  }

  return context;
}
