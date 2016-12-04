﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Microsoft.AspNetCore.Mvc;

namespace CoreSync.Controllers
{
    public class CalendarSettings
    {
        public string CalendarId { get; set; }

        public string ServiceAccountEmail { get; set; }

        public string GooglePrivateKey { get; set; }

        public string ApplicationName { get; set; }
    }

    [Route("api/[controller]")]
    public class CalendarController : Controller
    {
        private readonly CalendarSettings _settings;

        public CalendarController()
        {
            _settings = new CalendarSettings
            {
                ApplicationName = Environment.GetEnvironmentVariable("ApplicationName"),
                CalendarId = Environment.GetEnvironmentVariable("CalendarId"),
                GooglePrivateKey = Environment.GetEnvironmentVariable("GooglePrivateKey"),
                ServiceAccountEmail = Environment.GetEnvironmentVariable("ServiceAccountEmail"),
            };
        }

        [HttpPost]
        public async Task<IActionResult> Post()
        {
            try
            {
                string body;
                using (var reader = new StreamReader(Request.Body))
                {
                    body = await reader.ReadToEndAsync();
                }

                var eventz = body.Split(new[] {"->"}, StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => new CalendarEvent(e))
                    .ToList();

                var credential = new ServiceAccountCredential(
                    new ServiceAccountCredential.Initializer(_settings.ServiceAccountEmail)
                    {
                        Scopes = new[] {CalendarService.Scope.Calendar}
                    }.FromPrivateKey(_settings.GooglePrivateKey));

                // Create the service.
                var service = new CalendarService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = _settings.ApplicationName
                });

                var listRequest =
                    service.Events.List(_settings.CalendarId);
                listRequest.TimeMin = DateTime.Now.Date;

                // Take the last date of the passed events to determine the range
                // By having the same range we're able to cleanup deleted events not found in the Google calendar
                var upperLimit = eventz.Any()
                    ? eventz.OrderByDescending(e => e.DateTimeIdentifier).FirstOrDefault().End
                    : DateTime.Now.AddDays(10);

                listRequest.TimeMax = upperLimit;
                listRequest.ShowDeleted = false;

                // Dealing with recurring events just causes more effort for this simple use case so we treat them as single events
                listRequest.SingleEvents = true;
                listRequest.MaxResults = 1000;
                listRequest.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

                // List events.
                var eventsFromGoogle = listRequest.Execute();

                var eventsToDelete = eventsFromGoogle.Items
                    .Where(gEvent => !eventz.Any(z => z.IsEqualToGoogle(gEvent))).ToList();

                foreach (var @event in eventsToDelete)
                {
                    var deleteRequest = new EventsResource.DeleteRequest(service, _settings.CalendarId, @event.Id);
                    await deleteRequest.ExecuteAsync();
                }

                // We get all the new events, that are not stored in the Google calendar so far
                // Not very efficient code, but for these few events it will not hurt that much :-)
                var todo = eventz.Where(e => !eventsFromGoogle.Items.Any(e.IsEqualToGoogle)).ToList();

                foreach (var calendarEvent in todo)
                {
                    var begin = new EventDateTime {DateTime = calendarEvent.Start};
                    var end = new EventDateTime {DateTime = calendarEvent.End};

                    var @event = new Event
                    {
                        Start = begin,
                        End = end,
                        Created = DateTime.Now,
                        Summary = calendarEvent.Title.StartsWith("Work@", StringComparison.OrdinalIgnoreCase)
                            ? $"{calendarEvent.Title} (via Phone)"
                            : "n/a"
                    };

                    // Using ical doesn't make it easier -> We use our own datetime based identifier
                    // @event.ICalUID = calendarEvent.Id;
                    var insertRequest = service.Events.Insert(@event, _settings.CalendarId);

                    var result = await insertRequest.ExecuteAsync();
                }


                return Ok($"Created {todo.Count}; Removed {eventsToDelete.Count()}");
            }

            catch (Exception e)
            {
                // Most likely the input was not correct; Not a nice errror handling here but sufficient
                return BadRequest(e.Message);
            }
        }
    }
}