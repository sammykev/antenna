declare module "@paystack/inline-js" {
  type Transaction = {
    key: string; email: string; amount: number; currency?: string; reference?: string; metadata?: object;
    onSuccess?: (tx: { reference: string }) => void; onCancel?: () => void;
  };
  export default class PaystackPop {
    newTransaction(options: Transaction): void;
  }
}
