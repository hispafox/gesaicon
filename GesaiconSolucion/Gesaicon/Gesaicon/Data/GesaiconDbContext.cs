using Microsoft.EntityFrameworkCore;
using Gesaicon.Models;

namespace Gesaicon.Data
{
    public class GesaiconDbContext : DbContext
    {
        public GesaiconDbContext(DbContextOptions<GesaiconDbContext> options) : base(options) { }

        public DbSet<ExpenseTicket> ExpenseTickets { get; set; }
    }
}
