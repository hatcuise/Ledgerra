import type { Account, AccountIconKind, Transaction } from "../types";

const ICON_CLASS: Record<AccountIconKind, string> = {
  Bank: "is-bank",
  Piggy: "is-piggy",
  Card: "is-card",
  Cash: "is-cash",
  Chart: "is-chart",
  Users: "is-users"
};

export function accountIconClass(iconKind: AccountIconKind): string {
  return ICON_CLASS[iconKind] ?? "is-bank";
}

export const ACCOUNT_GROUP_ORDER = [
  "Checking", "Savings", "Credit", "Cash", "Investment", "Mortgage", "Joint"
] as const;

export type AccountGroupType = (typeof ACCOUNT_GROUP_ORDER)[number];

export type AccountGroup = {
  type: AccountGroupType;
  accounts: Account[];
  totalBalance: number;
  currencyCode: string | null;
};

export function groupAccountsByType(accounts: Account[]): AccountGroup[] {
  return ACCOUNT_GROUP_ORDER
    .map((type) => {
      const matches = accounts.filter((a) => a.type === type);
      if (matches.length === 0) {
        return null;
      }
      const totalBalance = matches.reduce((sum, a) => sum + a.currentBalance, 0);
      const currencies = new Set(matches.map((a) => a.currencyCode));
      const currencyCode = currencies.size === 1 ? matches[0].currencyCode : null;
      return { type, accounts: matches, totalBalance, currencyCode };
    })
    .filter((g): g is AccountGroup => g !== null);
}

export function filterAccounts(accounts: Account[], query: string): Account[] {
  const q = query.trim().toLowerCase();
  if (!q) return accounts;
  return accounts.filter((a) => {
    const haystack = [
      a.name,
      a.institutionName ?? "",
      a.accountNumberMasked ?? ""
    ].join(" ").toLowerCase();
    return haystack.includes(q);
  });
}

export type NetWorth = { value: number; currencyCode: string | null };

export function computeNetWorth(accounts: Account[]): NetWorth {
  const includedAccounts = accounts.filter((a) => !a.excludeFromNetWorth);
  if (includedAccounts.length === 0) {
    const currencies = new Set(accounts.map((a) => a.currencyCode));
    const currencyCode = accounts.length > 0 && currencies.size === 1 ? accounts[0].currencyCode : null;
    return { value: 0, currencyCode };
  }
  const value = includedAccounts.reduce((sum, a) => sum + a.currentBalance, 0);
  const currencies = new Set(includedAccounts.map((a) => a.currencyCode));
  const currencyCode = currencies.size === 1 ? includedAccounts[0].currencyCode : null;
  return { value, currencyCode };
}

export type BalancePoint = { date: string; balance: number };

export function signedAmount(t: Transaction): number {
  if (t.parentTransactionId) return 0;
  if (t.type === "Expense" || t.type === "TransferOut") return -Math.abs(t.amount);
  if (t.type === "Income" || t.type === "Refund" || t.type === "TransferIn") return Math.abs(t.amount);
  return t.amount;
}

function toDateKey(d: Date): string {
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, "0")}-${String(d.getUTCDate()).padStart(2, "0")}`;
}

export function computeWeekChange(transactions: Transaction[], now: Date): number {
  const sevenDaysAgo = new Date(now);
  sevenDaysAgo.setUTCDate(sevenDaysAgo.getUTCDate() - 7);
  return transactions
    .filter((t) => {
      const d = new Date(t.occurredOnUtc);
      return d >= sevenDaysAgo && d <= now;
    })
    .reduce((sum, t) => sum + signedAmount(t), 0);
}

function monthKey(value: string): string {
  return value.slice(0, 7);
}

export function computeMonthInflows(transactions: Transaction[], month: string): number {
  return transactions
    .filter((t) => !t.parentTransactionId && monthKey(t.occurredOnUtc) === month && (t.type === "Income" || t.type === "Refund" || t.type === "TransferIn"))
    .reduce((sum, t) => sum + Math.abs(t.amount), 0);
}

export function computeMonthOutflows(transactions: Transaction[], month: string): number {
  return transactions
    .filter((t) => !t.parentTransactionId && monthKey(t.occurredOnUtc) === month && (t.type === "Expense" || t.type === "TransferOut"))
    .reduce((sum, t) => sum + Math.abs(t.amount), 0);
}

export function computeBalanceSeries(args: {
  currentBalance: number;
  transactions: Transaction[];
  rangeDays: number;
  now: Date;
}): BalancePoint[] {
  const { currentBalance, transactions, rangeDays, now } = args;
  const sorted = [...transactions].sort((a, b) => a.occurredOnUtc.localeCompare(b.occurredOnUtc));

  const start = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  if (rangeDays > 0) start.setUTCDate(start.getUTCDate() - rangeDays);
  const end = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));

  const txByDay = new Map<string, number>();
  for (const t of sorted) {
    const key = toDateKey(new Date(t.occurredOnUtc));
    txByDay.set(key, (txByDay.get(key) ?? 0) + signedAmount(t));
  }

  const points: BalancePoint[] = [];
  let balance = currentBalance;
  const cursor = new Date(end);

  while (cursor >= start) {
    const key = toDateKey(cursor);
    points.unshift({ date: key, balance });
    balance -= txByDay.get(key) ?? 0;
    cursor.setUTCDate(cursor.getUTCDate() - 1);
  }
  return points;
}
