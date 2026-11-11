/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>The sign-in methods on offer: what the service's check found, what a sign-in description offers, what both hold, and how an offer reads in a log line.</summary>
public sealed class SignInOfferTests
{
   /// <summary>The check's methods are read by the names the contract gives them, exactly; a name the companion does not know, or one in another letter case, is ignored.</summary>
   [Theory]
   [InlineData("password", true, false)]
   [InlineData("browser", false, true)]
   [InlineData("browser,password", true, true)]
   [InlineData("passkey,Password,BROWSER", false, false)]
   [InlineData("", false, false)]
   public void Checked_ReadsTheMethodsTheContractNames(string methods, bool password, bool browser)
   {
      Assert.Equal(new SignInOffer(password, browser), SignInOffer.Checked(new ServerCheckResult([], methods.Split(',', StringSplitOptions.RemoveEmptyEntries))));
   }



   /// <summary>A description offers what the service's check would find in it: a table-based server takes a password and nothing else, even when it also publishes a browser grant; any other server offers the browser when it publishes the grant, and nothing without it.</summary>
   [Fact]
   public void Read_FollowsTheRuleOfTheServicesCheck()
   {
      QqqAuthenticationMetadata grant = SignInWorld.BrowserDescription();

      Assert.Equal(new SignInOffer(true, false), SignInOffer.Read(new QqqAuthenticationMetadata { Type = "TABLE_BASED" }));
      Assert.Equal(new SignInOffer(true, false), SignInOffer.Read(new QqqAuthenticationMetadata { Type = "TABLE_BASED", Values = grant.Values }));
      Assert.Equal(new SignInOffer(false, true), SignInOffer.Read(grant));
      Assert.Equal(new SignInOffer(false, false), SignInOffer.Read(new QqqAuthenticationMetadata { Type = "OAUTH2" }));
      Assert.Equal(new SignInOffer(false, false), SignInOffer.Read(new QqqAuthenticationMetadata { Type = "OAUTH2", Values = new QqqAuthenticationMetadata.ProviderValues() }));
      Assert.Equal(new SignInOffer(false, false), SignInOffer.Read(new QqqAuthenticationMetadata { Type = "table_based" }));
   }



   /// <summary>What two offers have in common is each method both hold, and an offer is something only when it holds a method.</summary>
   [Fact]
   public void Common_IsWhatBothHold()
   {
      Assert.Equal(new SignInOffer(true, false), new SignInOffer(true, true).Common(new SignInOffer(true, false)));
      Assert.Equal(new SignInOffer(false, true), new SignInOffer(false, true).Common(new SignInOffer(true, true)));
      Assert.Equal(new SignInOffer(false, false), new SignInOffer(true, false).Common(new SignInOffer(false, true)));
      Assert.True(new SignInOffer(true, false).Any);
      Assert.True(new SignInOffer(false, true).Any);
      Assert.False(new SignInOffer(false, false).Any);
   }



   /// <summary>An offer reads in a log line by the names the contract gives the methods.</summary>
   [Theory]
   [InlineData(true, true, "password and browser")]
   [InlineData(true, false, "password")]
   [InlineData(false, true, "browser")]
   [InlineData(false, false, "no method")]
   public void ToString_NamesTheMethods(bool password, bool browser, string text)
   {
      Assert.Equal(text, new SignInOffer(password, browser).ToString());
   }



   /// <summary>Each of the three needs what it reads from.</summary>
   [Fact]
   public void MissingArgument_IsRefused()
   {
      Assert.Equal("check", Assert.Throws<ArgumentNullException>(() => SignInOffer.Checked(null!)).ParamName);
      Assert.Equal("description", Assert.Throws<ArgumentNullException>(() => SignInOffer.Read(null!)).ParamName);
      Assert.Equal("other", Assert.Throws<ArgumentNullException>(() => new SignInOffer(true, true).Common(null!)).ParamName);
   }
}
