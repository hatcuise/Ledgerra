import { CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import type { SpendingInsights } from "../types/insights";
import { formatCurrency } from "../utils/format";
export function SpendingPaceChart({ data }: { data: SpendingInsights }) {
  return <div className="insight-chart" role="img" aria-label="Cumulative net spending, prior-month average and estimated month-end pace">
    <ResponsiveContainer width="100%" height={280} minWidth={0}>
      <LineChart data={data.daily} margin={{ top: 12, right: 18, bottom: 8, left: 8 }}>
        <CartesianGrid strokeDasharray="3 3" vertical={false} /><XAxis dataKey="day" /><YAxis width={78} tickFormatter={value => new Intl.NumberFormat(undefined, { notation: "compact" }).format(Number(value))} />
        <Tooltip labelFormatter={day => `Day ${day}`} formatter={value => formatCurrency(Number(value), data.currencyCode)} /><Legend />
        <Line type="linear" dataKey="current" name="Recorded net spending" stroke="#27a67c" strokeWidth={3} dot={false} isAnimationActive={false} />
        {data.historicalAverage !== null && <Line type="linear" dataKey="historicalAverage" name="Prior-month average" stroke="#689bdd" strokeWidth={2} dot={false} isAnimationActive={false} />}
        {data.projectedMonthEnd !== null && <Line type="linear" dataKey="projected" name="Estimated pace" stroke="#d5943c" strokeWidth={2} strokeDasharray="6 4" dot={false} isAnimationActive={false} />}
      </LineChart>
    </ResponsiveContainer>
  </div>;
}
