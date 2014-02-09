using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BankAccount.Tests
{
    [TestFixture]
    public class BankAccountTests
    {
        [Test]
        public void TransferFunds()
        {
            BankAccount source = new BankAccount(0m, 0m);
            source.Deposit(200m);

            BankAccount destination = new BankAccount(50m, 100m);
            destination.Deposit(150m);

            source.TransferFunds(destination, 100m);

            Assert.AreEqual(350m, destination.Balance);
            Assert.AreEqual(100m, source.Balance);
        }

        [Test]
        public void DepositFunds()
        {
            BankAccount a = new BankAccount(0m, 10m);
            a.Deposit(100m);
            Assert.AreEqual(110m, a.Balance);
        }

        [Test]
        public void WithdrawFundsFailure()
        {
            BankAccount a = new BankAccount(10m, 20m);
            try
            {
                a.Withdraw(20m);
            }
            catch (InsufficientFundsException)
            {
            }
            Assert.AreEqual(20m, a.Balance);
        }

        [Test]
        public void CloseAccount()
        {
            BankAccount a = new BankAccount(0m, 0m);
            a.CloseAccount();
            Assert.AreEqual(false, a.IsActive);
        }
    }
}
