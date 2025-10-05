using Microsoft.EntityFrameworkCore;
using Gesaicon.Api.Models;

namespace Gesaicon.Api.Data
{
    public class GesaiconDbContext : DbContext
    {
        public GesaiconDbContext(DbContextOptions<GesaiconDbContext> options) : base(options) { }

        public DbSet<ExpenseTicket> ExpenseTickets { get; set; }
    }
}
