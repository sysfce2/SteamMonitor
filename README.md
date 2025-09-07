SteamMonitor is a console application that continuously monitors Steam connection manager servers. Steam CM is a server that the Steam desktop clients connect to.

It maintains connections to a few servers in every available datacenter, and whenever it gets disconnected, the status gets updated in the MySQL database (see `schema.sql` for the table definition).

Database access is configured by creating a `database.txt` file in the application directory containing your MySQL connection string.

[steamstat.us](https://steamstat.us/) uses this database to display status of the servers per region.
