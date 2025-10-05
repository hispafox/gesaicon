using Microsoft.EntityFrameworkCore;
using Gesaicon.Api.Models;

namespace Gesaicon.Api.Data
{
    public class GesaiconDbContext : DbContext
    {
        public GesaiconDbContext(DbContextOptions<GesaiconDbContext> options) : base(options) { }

        public DbSet<ExpenseTicket> ExpenseTickets { get; set; }
        public DbSet<AnalysisLog> AnalysisLogs { get; set; } // nuevo

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Índice filtrado para evitar duplicados de archivos (permite NULL)
            modelBuilder.Entity<ExpenseTicket>()
                .HasIndex(e => e.FileHash)
                .HasDatabaseName("IX_ExpenseTickets_FileHash")
                .IsUnique(false) // se podría poner true; mantenemos false por si hay colisión extremadamente rara
                .HasFilter("[FileHash] IS NOT NULL");
        }
    }
}
