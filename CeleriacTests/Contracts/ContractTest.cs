using System;
using System.Diagnostics.Contracts;

namespace Bank
{
    public class TestDriver
    {
        public static int Main()
        {
            Account x = new Account(100);
            Account y = new Account(50);
            y.Deposit(50);
            x.TransferFunds(y, 25);

            return 0;
        }
    }

    [ContractClass(typeof(AccountContracts))]
    public interface IAccount
    {
        void Deposit(decimal amount);
        void Withdraw(decimal amount);
        void TransferFunds(IAccount destination, decimal amount);
        decimal Balance { get; }
    }

    [ContractClassFor(typeof(IAccount))]
    public abstract class AccountContracts : IAccount
    {
        public void Deposit(decimal amount)
        {
            Contract.Requires<ArgumentException>(amount > 0);
        }

        public void Withdraw(decimal amount)
        {
            Contract.Requires<ArgumentException>(amount > 0);
        }

        public void TransferFunds(IAccount destination, decimal amount)
        {
            Contract.Requires(destination != null);
            Contract.Requires<ArgumentException>(amount > 0);
            Contract.Ensures(destination.Balance == Contract.OldValue(destination.Balance) + amount);
            Contract.Ensures(Balance == Contract.OldValue(this.Balance) - amount);
        }

        public decimal Balance
        {
            get { return 0.0m; }
        }
    }

    public class Account : IAccount
    {
        private decimal balance;
        public const decimal MinimumBalance = 10m;

        [ContractInvariantMethod]
        private void ObjectInvariant()
        {
            Contract.Invariant(Balance >= MinimumBalance);
        }

        public Account(decimal initialBalance)
        {
            Contract.Requires<ArgumentException>(initialBalance >= MinimumBalance);
            Contract.Ensures(Balance == initialBalance);
            this.balance = initialBalance;
        }

        /// <summary>
        /// Deposit money into the account.
        /// </summary>
        /// <param name="amount">a non-negative amount</param>
        public void Deposit(decimal amount)
        {
            balance += amount;
        }

        /// <summary>
        /// Withdraw money from the account.
        /// </summary>
        /// <param name="amount">a non-negative amount</param>
        public void Withdraw(decimal amount)
        {
            balance -= amount;
        }

        /// <summary>
        /// Transfers <c>amount</c> from this account to <c>destination</c>.
        /// </summary>
        /// <param name="destination">the destination account, cannot be <c>null</c></param>
        /// <param name="amount">a non-negative amount</param>
        /// <exception cref="InsufficientFundsException">if the source account has insufficient funds</exception>
        public void TransferFunds(IAccount destination, decimal amount)
        {
            Contract.EnsuresOnThrow<InsufficientFundsException>(Balance - amount >= MinimumBalance);
            Contract.EnsuresOnThrow<InsufficientFundsException>(Contract.OldValue(destination).Balance == destination.Balance);
            Contract.EnsuresOnThrow<InsufficientFundsException>(Contract.OldValue(Balance) == Balance);

            destination.Deposit(amount);
            Withdraw(amount);
        }

        /// <summary>
        /// The account balance
        /// </summary>
        public decimal Balance
        {
            get
            {
                Contract.Ensures(Contract.Result<decimal>() == balance);
                return balance;
            }
        }
    }

    public class InsufficientFundsException : ApplicationException
    {
    }
}