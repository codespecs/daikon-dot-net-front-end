using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BankAccount
{
    /// <summary>
    /// An account with a non-negative balance.
    /// </summary>
    public class BankAccount : IAccount
    {
        private decimal balance;
        private decimal minimumBalance;
        private bool closed = false;

        [ContractInvariantMethod]
        private void ObjectInvariant()
        {
            // Inferred object invariants will go here
        }

        /// <summary>
        /// Create an account with starting balance <c>initialBalance</c>.
        /// </summary>
        /// <param name="minimumBalance">the minumum account balance</param>
        /// <param name="initialBalance">the starting balance</param>
        public BankAccount(decimal minimumBalance, decimal initialBalance)
        {
            this.minimumBalance = minimumBalance;
            this.balance = initialBalance;
        }

        public void Deposit(decimal amount)
        {
            balance += amount;
        }

        public void Withdraw(decimal amount)
        {
            if (balance - amount < minimumBalance)
            {
                throw new InsufficientFundsException();
            }

            balance -= amount;
        }

        public void TransferFunds(IAccount destination, decimal amount)
        {
            if (balance - amount < minimumBalance)
            {
                throw new InsufficientFundsException();
            }

            Withdraw(amount);
            destination.Deposit(amount);
        }

        public decimal MinimumBalance
        {
            get { return minimumBalance; }
        }

        /// <summary>
        /// Close the account.
        /// </summary>
        public void CloseAccount()
        {
            closed = true;
        }

        public decimal Balance
        {
            get { return balance; }
        }

        public bool IsActive
        {
            get { return !closed; }
        }
    }

    public class InsufficientFundsException : ApplicationException
    {
    }
}
