using Microsoft.EntityFrameworkCore;
using TyzenR.Account;
using TyzenR.Account.Entity;
using TyzenR.Account.Managers;
using TyzenR.EntityLibrary;
using TyzenR.Publisher.Shared;
using TyzenR.Taskman.Entity;
using TyzenR.Taskman.Entity.Models;

namespace TyzenR.Taskman.Managers
{
    public class TaskManager : BaseRepository<TaskEntity>, ITaskManager
    {
        private readonly EntityContext entityContext;
        private readonly AccountContext accountContext;
        private readonly IUserManager userManager;
        private readonly IActionTrackerManager actionManager;
        private readonly IAttachmentManager attachmentManager;
        private readonly IAppInfo appInfo;

        public TaskManager(
            EntityContext entityContext,
            AccountContext accountContext,
            IUserManager userManager,
            IActionTrackerManager actionManager,
            IAttachmentManager attachmentManager,
            IAppInfo appInfo) : base(entityContext)
        {
            this.entityContext = entityContext ?? throw new ApplicationException("Instance is null!");
            this.accountContext = accountContext ?? throw new ApplicationException("Instance is null!");
            this.userManager = userManager ?? throw new ApplicationException("Instance is null!");
            this.actionManager = actionManager ?? throw new ApplicationException("Instance is null!");
            this.attachmentManager = attachmentManager ?? throw new ApplicationException("Instance is null!");
            this.appInfo = appInfo ?? throw new ApplicationException("Instance is null!");
        }

        public async Task<IList<UserEntity>> GetManagersAsync(UserEntity user)
        {
            if (user == null)
            {
                return new List<UserEntity>();
            }

            var managerIds = await this.entityContext.Teams
                .Where(t => t.MemberId == user.Id)
                .Select(t => t.ManagerId)
                .ToListAsync();

            var users = await this.accountContext.Users
                .Where(u => managerIds.Contains(u.Id))
                .ToListAsync();

            return users;
        }

        public async Task<IList<TaskEntity>> GetPaginatedTasksForUserAsync(IQueryable<TaskEntity> query, int page, int pageSize, string sortBy, SortDirectionEnum direction)
        {
            switch (sortBy)
            {
                case "Title":
                    query = direction == SortDirectionEnum.Ascending
                        ? query.OrderBy(e => e.Title)
                        : query.OrderByDescending(e => e.Title);
                    break;

                case "Description":
                    query = direction == SortDirectionEnum.Ascending
                        ? query.OrderBy(e => e.Description)
                        : query.OrderByDescending(e => e.Description);
                    break;

                case "Status":
                    query = direction == SortDirectionEnum.Ascending
                        ? query.OrderBy(e => e.Status)
                        : query.OrderByDescending(e => e.Status);
                    break;

                case "AssignedTo":
                    query = direction == SortDirectionEnum.Ascending
                        ? query.OrderBy(e => e.AssignedTo)
                        : query.OrderByDescending(e => e.AssignedTo);
                    break;

                case "UpdatedOn":
                    query = direction == SortDirectionEnum.Ascending
                        ? query.OrderBy(e => e.UpdatedOn)
                        : query.OrderByDescending(e => e.UpdatedOn);
                    break;

                default:
                    query = query.OrderBy(t => t.Status)
                        .ThenByDescending(t => t.UpdatedOn);
                    break;
            }

            var result = await query.Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Populate AssignedToName for each task    
            foreach (var task in result)
            {
                if (idNameDictionary.ContainsKey(task.AssignedTo))
                {
                    task.AssignedToName = idNameDictionary[task.AssignedTo];
                }
                else
                {
                    var user = userManager.GetUserById(task.AssignedTo);
                    if (user != null)
                    {
                        task.AssignedToName = user.FirstName;
                        idNameDictionary.Add(task.AssignedTo, task.AssignedToName);
                    }
                }
            }

            return result;
        }

        private IDictionary<Guid, string> idNameDictionary = new Dictionary<Guid, string>();

        public async Task<IList<TaskEntity>> GetTasksForUserAsync(UserEntity user)
        {
            if (user == null)
            {
                return new List<TaskEntity>();
            }

            var result = await this.entityContext.Tasks
                .Where(t => (t.Status == TaskStatusEnum.InProgress || t.Status == TaskStatusEnum.Completed) && (t.CreatedBy == user.Id || t.AssignedTo == user.Id))
                .OrderBy(t => t.Status)
                .ThenByDescending(t => t.UpdatedOn)
                .ToListAsync();

            return result;
        }

        public async Task<IList<TeamMemberModel>> GetTeamMembersAsync(UserEntity user, string firstItem = "")
        {
            if (user == null)
            {
                if (string.IsNullOrEmpty(firstItem))
                {
                    return new List<TeamMemberModel>();
                }

                return new List<TeamMemberModel>()
                {
                    new TeamMemberModel() { Id = Guid.Empty, Name = firstItem }
                };
            }

            var members = await this.entityContext.Teams
                .Where(t => t.ManagerId == user.Id)
                .ToListAsync();

            var users = await this.accountContext.Users
                .Where(u => members.Select(m => m.MemberId).Contains(u.Id))
                .ToListAsync();

            var result = new List<TeamMemberModel>();
            result.Add(new TeamMemberModel() { Id = user.Id, Name = user.FirstName });
            foreach (var member in members)
            {
                if (!result.Any(r => r.Id == member.MemberId))
                {
                    result.Add(new TeamMemberModel() { Id = member.MemberId, Name = users.FirstOrDefault(u => u.Id == member.MemberId)?.FirstName });
                }
            }

            result.Insert(0, new TeamMemberModel() { Id = Guid.Empty, Name = firstItem });

            return result;
        }

        public async Task NotifyManagersAsync(UserEntity user, TaskEntity task, string title)
        {
            string body = string.Empty;

            try
            {
                var managers = await GetManagersAsync(user);

                if (managers.Count == 0)
                {
                    managers.Add(new UserEntity() { Email = "jeanpaulva@gmail.com" });
                }

                foreach (var manager in managers)
                {
                    if (task.Type == TaskTypeEnum.Task)
                    {
                        body = "User:".Bold() + $" {user.FirstName}".Break() +
                            "Title: ".Bold() + $"{task.Title}".Break() +
                            "Description: ".Bold() + $"{task.Description.AddBreaks()}".Break() +
                            "Status: ".Bold() + $"{task.Status.ToString()}".Break() +
                            "UpdatedOn: ".Bold() + $"{task.UpdatedOn}".Break() +
                            "Url: ".Bold() + GetUrl(task).Break();
                    }
                    else if (task.Type == TaskTypeEnum.Timesheet)
                    {
                        body = "User:".Bold() + $" {user.FirstName}".Break() +
                         "Title: ".Bold() + $"{task.Title}".Break() +
                         "Date: ".Bold() + $"{task.Date}".Break();

                        foreach (var item in task.GetTimesheetItems())
                        {
                            if (item.Hours > 0 || item.Minutes > 0)
                            {
                                body += $"{item.Description} {item.Hours} hours {item.Minutes} minutes".Break();
                            }
                        }

                        body += "Notes: ".Bold() + $"{task.Notes.AddBreaks()}".Break() +
                            "Total: ".Bold() + task.GetTotalTimeFormatted().Break() +
                            "UpdatedOn: ".Bold() + $"{task.UpdatedOn}".Break() +
                            "Url: ".Bold() + GetUrl(task).Break();

                        if (manager.Email == "contact@futurecaps.com")
                        {
                            body += $"UpdatedIP: {task.UpdatedIP}".Break();
                        }

                        var attachments = await attachmentManager.GetAllByParentIdAsync(task.Id);
                        List<string> stringAttachments = await attachmentManager.GetStringAttachmentsAsync(attachments);

                        await appInfo.SendEmailAsync(manager.Email, title, body, false, stringAttachments);
                    }
                }
            }
            catch (Exception ex)
            {
                await SharedUtility.SendEmailToModeratorAsync("Taskman.TaskManager.NotifyManagersAsync.Exception", "ip: " + appInfo.CurrentUserIPAddress + "  " + ex.ToString().Break() + body);
            }
        }

        private string GetUrl(TaskEntity task)
        {
            if (task.Type == TaskTypeEnum.Task)
            {
                return $"{TaskmanConstants.ApplicationUrl}/task/edit/{task.Id}";
            }
            else
            {
                return $"{TaskmanConstants.ApplicationUrl}/task/timesheet/edit/{task.Id}";
            }

            throw new NotImplementedException();
        }

        public async Task NotifyUserAsync(TaskEntity task, string title)
        {
            try
            {
                if (task.AssignedTo != appInfo.CurrentUserId)
                {
                    var user = await userManager.GetByIdAsync(task.AssignedTo);
                    await appInfo.SendEmailAsync(user.Email, title, $"Visit: {TaskmanConstants.ApplicationUrl} for details");
                }
            }
            catch (Exception ex)
            {
                await SharedUtility.SendEmailToModeratorAsync("Taskman.TaskManager.NotifyUserAsync.Exception", "ip: " + appInfo.CurrentUserIPAddress + "  " + ex.ToString().Break());
            }
        }

        public async Task<IList<TaskEntity>> GetTimesheetsAsync(Guid userId, DateTime fromDate, DateTime toDate)
        {
            try
            {
                var timesheets = await this.entityContext.Tasks
                    .Where(t => t.Type == TaskTypeEnum.Timesheet &&
                           t.AssignedTo == userId &&
                           t.Date >= fromDate && t.Date <= toDate)
                    .ToListAsync();

                return timesheets;
            }
            catch (Exception ex)
            {
                await SharedUtility.SendEmailToModeratorAsync("Taskman.TaskManager.GetTimesheetsAsync.Exception", ex.ToString().Break());
            }

            return new List<TaskEntity>();
        }

        public string GetTotalTimeFormatted(IList<TaskEntity> list)
        {
            int totalHours = 0, totalMinutes = 0;
            foreach (var item in list)
            {
                totalHours = totalHours + item.GetTimesheetItems().Sum(t => t.Hours);
                totalMinutes = totalMinutes + item.GetTimesheetItems().Sum(t => t.Minutes);
            }

            totalHours = totalHours + (totalMinutes / 60);
            totalMinutes = totalMinutes % 60;

            return $"{totalHours}h {totalMinutes}m";
        }

        public int GetCountOf(TaskStatusEnum status, Guid userId)
        {
            try
            {
                var count = entityContext.Tasks.Count(t => t.AssignedTo == userId && t.Status == status);

                return count;
            }
            catch (Exception ex)
            {
                SharedUtility.SendEmailToModeratorAsync("Taskman.TaskManager.GetCountOf.Exception", ex.ToString().Break()).Wait();
            }

            return 0;
        }

        public IList<TeamMemberInProgressModel> GetTeamInProgressData()
        {
            IList<TeamMemberInProgressModel> result = new List<TeamMemberInProgressModel>();
            {
                new TeamMemberInProgressModel { Id = appInfo.CurrentUserId, Name = appInfo.GetCurrentUser().FirstName, Count = GetCountOf(TaskStatusEnum.InProgress, appInfo.CurrentUserId) };
            }

            var teams = entityContext.Teams.Where(t => t.ManagerId == appInfo.CurrentUserId).ToList();
            foreach (var team in teams)
            {
                result.Add(new TeamMemberInProgressModel { Id = team.Id, Name = team.Name.GetFirstName(), Count = GetCountOf(TaskStatusEnum.InProgress, team.Id) });
            }

            return result;
        }
    }
}
