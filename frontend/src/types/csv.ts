export type CsvOptions = {
  payeeColumn: string; categoryColumn: string; referenceColumn: string; tagsColumn: string;
  delimiter: string; cultureName: string; dateFormat: string;
  debitColumn: string; creditColumn: string; typeColumn: string; positiveType: string; negativeType: string;
};
export const defaultCsvOptions: CsvOptions = { payeeColumn: "", categoryColumn: "", referenceColumn: "", tagsColumn: "", delimiter: ",", cultureName: "en-US", dateFormat: "", debitColumn: "", creditColumn: "", typeColumn: "", positiveType: "Income", negativeType: "Expense" };
export type CsvProfile = { id: string; name: string; settingsJson: string };

