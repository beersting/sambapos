﻿using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using Samba.Domain.Models.Menus;
using Samba.Domain.Models.Settings;
using Samba.Domain.Models.Tickets;
using Samba.Domain.Models.Users;
using Samba.Localization.Properties;
using Samba.Persistance.Data;
using Samba.Presentation.Common;
using Samba.Presentation.Common.Services;
using Samba.Services;

namespace Samba.Presentation.ViewModels
{
    public static class GenericRuleRegistator
    {
        private static bool _ran;
        public static void RunOnce()
        {
            Debug.Assert(_ran == false);
            RegisterActions();
            RegisterRules();
            RegisterParameterSources();
            HandleEvents();
            RegisterNotifiers();
            _ran = true;
        }

        private static void RegisterActions()
        {
            RuleActionTypeRegistry.RegisterActionType("SendEmail", "Send Email", "SMTPServer", "SMTPUser", "SMTPPassword", "SMTPPort", "ToEMailAddress", "Subject", "FromEMailAddress", "EMailMessage", "FileName", "DeleteFile");
            RuleActionTypeRegistry.RegisterActionType("AddTicketDiscount", "Add Ticket Discount", "DiscountPercentage");
            RuleActionTypeRegistry.RegisterActionType("UpdateTicketTag", "Update Ticket Tag", "TagName", "TagValue");
            RuleActionTypeRegistry.RegisterActionType("UpdatePriceList", "Update Price List", "PriceTag");
            RuleActionTypeRegistry.RegisterActionType("RefreshPriceList", "Refresh Price List");
            RuleActionTypeRegistry.RegisterActionType("RefreshCache", "Refresh Cache");
            RuleActionTypeRegistry.RegisterActionType("SendActionMessage", "Send Action Message", "Command");
        }

        private static void RegisterRules()
        {
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.UserLoggedIn, Resources.UserLogin, new { UserName = "", RoleName = "" });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.UserLoggedOut, Resources.UserLogout, new { UserName = "", RoleName = "" });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.WorkPeriodStarts, Resources.WorkPeriodStarted, new { UserName = "" });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.WorkPeriodEnds, Resources.WorkPeriodEnded, new { UserName = "" });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.TriggerExecuted, "Trigger Executed", new { TriggerName = "" });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.TicketCreated, Resources.TicketCreated);
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.TicketTagSelected, Resources.TicketTagSelected, new { TagName = "", TagValue = "" });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.CustomerSelectedForTicket, Resources.CustomerSelectedForTicket, new { CustomerName = "", PhoneNumber = "", CustomerNote = "" });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.TicketTotalChanged, Resources.TicketTotalChanged, new { TicketTotal = 0m, DiscountTotal = 0m, GiftTotal = 0m });
            RuleActionTypeRegistry.RegisterEvent(RuleEventNames.ActionMessageReceived, "Action Message Received", new { Command = "" });
        }

        private static void RegisterParameterSources()
        {
            RuleActionTypeRegistry.RegisterParameterSoruce("UserName", () => AppServices.MainDataContext.Users.Select(x => x.Name));
            RuleActionTypeRegistry.RegisterParameterSoruce("DepartmentName", () => AppServices.MainDataContext.Departments.Select(x => x.Name));
            RuleActionTypeRegistry.RegisterParameterSoruce("TerminalName", () => AppServices.Terminals.Select(x => x.Name));
            RuleActionTypeRegistry.RegisterParameterSoruce("TriggerName", () => Dao.Select<Trigger, string>(yz => yz.Name, y => !string.IsNullOrEmpty(y.Expression)));
            RuleActionTypeRegistry.RegisterParameterSoruce("PriceTag", () => Dao.Select<MenuItemPriceDefinition, string>(x => x.PriceTag, x => x.Id > 0));
            RuleActionTypeRegistry.RegisterParameterSoruce("Color", () => typeof(Colors).GetProperties(BindingFlags.Public | BindingFlags.Static).Select(x => x.Name));
        }

        private static void ResetCache()
        {
            TriggerService.UpdateCronObjects();
            AppServices.ResetCache();
            AppServices.MainDataContext.SelectedDepartment.PublishEvent(EventTopicNames.SelectedDepartmentChanged);
        }

        private static void HandleEvents()
        {
            EventServiceFactory.EventService.GetEvent<GenericEvent<ActionData>>().Subscribe(x =>
            {
                if (x.Value.Action.ActionType == "RefreshCache")
                {

                    MethodQueue.Queue("ResetCache", ResetCache);
                }

                if (x.Value.Action.ActionType == "SendActionMessage")
                {
                    AppServices.MessagingService.SendMessage("ActionMessage", x.Value.GetAsString("Command"));
                }

                if (x.Value.Action.ActionType == "RefreshPriceList")
                {
                    PriceService.RebuildPricesIfNeeded();
                }

                if (x.Value.Action.ActionType == "UpdatePriceList")
                {
                    PriceService.ApplyPriceList(x.Value.GetAsString("PriceTag"));
                }

                if (x.Value.Action.ActionType == "SendEmail")
                {
                    EMailService.SendEMailAsync(x.Value.GetAsString("SMTPServer"),
                        x.Value.GetAsString("SMTPUser"),
                        x.Value.GetAsString("SMTPPassword"),
                        x.Value.GetAsInteger("SMTPPort"),
                        x.Value.GetAsString("ToEMailAddress"),
                        x.Value.GetAsString("FromEMailAddress"),
                        x.Value.GetAsString("Subject"),
                        x.Value.GetAsString("EMailMessage"),
                        x.Value.GetAsString("FileName"),
                        x.Value.GetAsBoolean("DeleteFile"));
                }

                if (x.Value.Action.ActionType == "AddTicketDiscount")
                {
                    var ticket = x.Value.GetDataValue<Ticket>("Ticket");
                    if (ticket != null)
                    {
                        var percentValue = x.Value.GetAsDecimal("DiscountPercentage");
                        ticket.AddTicketDiscount(DiscountType.Percent, percentValue, AppServices.CurrentLoggedInUser.Id);
                        TicketService.RecalculateTicket(ticket);
                    }
                }

                if (x.Value.Action.ActionType == "UpdateTicketTag")
                {
                    var ticket = x.Value.GetDataValue<Ticket>("Ticket");
                    if (ticket != null)
                    {
                        var tagName = x.Value.GetAsString("TagName");
                        var tagValue = x.Value.GetAsString("TagValue");
                        ticket.SetTagValue(tagName, tagValue);
                        var tagData = new TicketTagData { TagName = tagName, TagValue = tagValue };
                        tagData.PublishEvent(EventTopicNames.TagSelectedForSelectedTicket);
                    }
                }
            });
        }

        private static void RegisterNotifiers()
        {
            EventServiceFactory.EventService.GetEvent<GenericEvent<Message>>().Subscribe(x =>
            {
                if (x.Topic == EventTopicNames.MessageReceivedEvent && x.Value.Command == "ActionMessage")
                {
                    RuleExecutor.NotifyEvent(RuleEventNames.ActionMessageReceived, new { Command = x.Value.Data });
                }
            });

            EventServiceFactory.EventService.GetEvent<GenericEvent<User>>().Subscribe(x =>
            {
                if (x.Topic == EventTopicNames.UserLoggedIn)
                {
                    RuleExecutor.NotifyEvent(RuleEventNames.UserLoggedIn, new { User = x.Value, UserName = x.Value.Name, RoleName = x.Value.UserRole.Name });
                }

                if (x.Topic == EventTopicNames.UserLoggedOut)
                {
                    RuleExecutor.NotifyEvent(RuleEventNames.UserLoggedOut, new { User = x.Value, UserName = x.Value.Name, RoleName = x.Value.UserRole.Name });
                }
            });
        }
    }
}