CS2 Simple Vote provides all of the basic map voting functionality at a fraction of the resources.

NOTE:DO NOT UNLOAD THIS PLUGIN AND RELOAD IT ON-THE-FLY. IT WILL GENERATE DUPLICATE INSTANCES.



Commands are:

rtv - to RockTheVote prior to a map vote. Cannot be initiated during warmup or after a map vote.

nominate - to nominate a map to be included in the standard map vote. (Uses a list. specifying map coming soon)

nextmap - to display the next map following a map vote.

revote - to revote within the standard map vote window.



The CS2SimpleVote.json config is auto-created on first launch, and is laid out as follows:

{

  "steam_api_key": "YOURAPIKEY", //The API key the plugin uses to fetch map names  
  "collection_id": "COLLECTION_ID", //The collection that is passed to the API to fetch map names from  
  "vote_round": 3, //Which round the map vote initiates on. Will persist for the entire round.  
  "enable_rtv": true, //Enable RTV 
  "enable_nominate": true, //Enable Nominate  
  "nominate_per_page": 8, //The amount of maps displayed on a nominate screen per page. 
  "rtv_percentage": 0.6, //The percentage (as a decimal) of votes required to initiate an RTV vote.  
  "rtv_change_delay": 5, //The delay in seconds before a completed RTV vote will change the map.  
  "vote_options_count": 8, //The amount of map options in a standard vote or RTV vote.
  "vote_reminder_enabled": true, //Reprint the map list on an interval to remind the player to vote if they haven't.  
  "vote_reminder_interval": 30, //The map list reminder interval.  
  "server_name": "My CS2 Server", //For the You're Playing MAPNAME on SERVERNAME message
  "show_map_message": true, //Show the You're Playing MAPNAME on SERVERNAME message
  "map_message_interval": 300, //Interval in seconds of the map message
  "enable_recent_maps": true, //WIP. Will be a feature to omit recent maps from votes
  "recent_maps_count": 5, //The amount of recent maps to track
  "randomize_startup_map": true, //Randomize the startup map for collections on the server
  "ConfigVersion": 1  
  
}

