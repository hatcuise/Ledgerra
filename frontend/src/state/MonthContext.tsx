import { createContext, ReactNode, useContext, useMemo, useState } from "react";

type MonthSelectionContextValue = {
  selectedMonth: string;
  selectedYear: number;
  selectedMonthNumber: number;
  setSelectedMonth: (month: string) => void;
  goToPreviousMonth: () => void;
  goToNextMonth: () => void;
  goToCurrentMonth: () => void;
};

const MonthSelectionContext = createContext<MonthSelectionContextValue | null>(null);

function currentMonthKey() {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}`;
}

function isMonthKey(value: string) {
  return /^\d{4}-(0[1-9]|1[0-2])$/.test(value);
}

function shiftMonth(month: string, offset: number) {
  const [year, monthNumber] = month.split("-").map(Number);
  const date = new Date(Date.UTC(year, monthNumber - 1 + offset, 1));
  return date.toISOString().slice(0, 7);
}

function getMonthParts(month: string) {
  const [year, monthNumber] = month.split("-").map(Number);
  return { selectedYear: year, selectedMonthNumber: monthNumber };
}

export function MonthProvider({ children, initialMonth }: { children: ReactNode; initialMonth?: string }) {
  const [selectedMonth, setSelectedMonthState] = useState(() =>
    initialMonth && isMonthKey(initialMonth) ? initialMonth : currentMonthKey()
  );

  const value = useMemo<MonthSelectionContextValue>(() => {
    const { selectedYear, selectedMonthNumber } = getMonthParts(selectedMonth);

    return {
      selectedMonth,
      selectedYear,
      selectedMonthNumber,
      setSelectedMonth: (month: string) => {
        if (isMonthKey(month)) {
          setSelectedMonthState(month);
        }
      },
      goToPreviousMonth: () => setSelectedMonthState((month) => shiftMonth(month, -1)),
      goToNextMonth: () => setSelectedMonthState((month) => shiftMonth(month, 1)),
      goToCurrentMonth: () => setSelectedMonthState(currentMonthKey())
    };
  }, [selectedMonth]);

  return <MonthSelectionContext.Provider value={value}>{children}</MonthSelectionContext.Provider>;
}

export function useMonthSelection() {
  const context = useContext(MonthSelectionContext);

  if (!context) {
    throw new Error("useMonthSelection must be used within MonthProvider");
  }

  return context;
}
