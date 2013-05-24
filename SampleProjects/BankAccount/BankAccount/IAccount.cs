using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BankAccount
{
    [ContractClass(typeof(IAccountContracts))]
    public interface IAccount
    {
        /// <summary>
        /// Deposit money into the account.
        /// </summary>
        /// <param name="amount">the amount to deposit</param>
        void Deposit(decimal amount);

        /// <summary>
        /// Withdraw money from the account.
        /// </summary>
        /// <param name="amount">the amount to withdraw</param>
        void Withdraw(decimal amount);
        
        /// <summary>
        /// Transfer funds from this account to a destination account. 
        /// </summary>
        /// <param name="destination">the destination account</param>
        /// <param name="amount">the amount to transfer</param>
        void TransferFunds(IAccount destination, decimal amount);

        /// <summary>
        /// The current account balance.
        /// </summary>
        decimal Balance { get; }

        /// <summary>
        /// The account is currently active
        /// </summary>
        bool IsActive { get; }
    }

    [ContractClassFor(typeof(IAccount))]
    public abstract class IAccountContracts
    {
        public void Deposit(decimal amount)
        {
        }

        public void Withdraw(decimal amount)
        {
        }

        public void TransferFunds(IAccount destination, decimal amount)
        {
        }

        public decimal Balance
        {
            get { return 0.0m; }
        }

        public bool IsActive
        {
            get { return false; }
        }
    }
}
